// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stateless.Execution;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV6Result?> _getPayloadHandlerV6;
    private readonly IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>> _executionGetPayloadBodiesByHashV2Handler;
    private readonly IGetPayloadBodiesByRangeV2Handler _executionGetPayloadBodiesByRangeV2Handler;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.NewPayload.V5);

    public async Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadWithWitnessV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
    {
        ExecutionPayloadParams<ExecutionPayloadV3> executionPayloadParams = new(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);
        ResultWrapper<PayloadStatusV1> result = await NewPayload(executionPayloadParams, EngineApiVersions.NewPayload.V5);

        if (result.Result.ResultType != ResultType.Success || result.Data.Status != PayloadStatus.Valid)
        {
            return result;
        }

        try
        {
            result.Data.Witness = GenerateOpaqueWitness(executionPayloadParams.ExecutionPayload);
            return result;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"engine_newPayloadWithWitnessV5 failed: {exception}");
            return ResultWrapper<PayloadStatusV1>.Fail(exception.Message);
        }
    }

    public Task<ResultWrapper<StatelessPayloadStatusV1>> engine_executeStatelessPayloadV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[] opaqueWitness)
        => ExecuteStatelessPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), opaqueWitness, EngineApiVersions.NewPayload.V5);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V4);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.Handle(blockHashes);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(long start, long count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);

    private byte[] GenerateOpaqueWitness(ExecutionPayload executionPayload)
    {
        Block block = GetExecutionBlock(executionPayload, ignoreSuppliedBlockHash: false);
        BlockHeader parentHeader = _blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing)
            ?? throw new InvalidOperationException($"Missing parent header {block.ParentHash} for witness generation.");

        block.Header.TotalDifficulty = parentHeader.TotalDifficulty is null
            ? null
            : parentHeader.TotalDifficulty + block.Header.Difficulty;

        RecoverTxSenders(block);

        using IWitnessGeneratingBlockProcessingEnvScope scope = _witnessGeneratingBlockProcessingEnvFactory.CreateScope();
        IExistingBlockWitnessCollector witnessCollector = scope.Env.CreateExistingBlockWitnessCollector();
        using Witness witness = witnessCollector.GetWitnessForExistingBlock(parentHeader, block);

        return InputSerializer.Serialize(block, witness, _specProvider.ChainId);
    }

    private Task<ResultWrapper<StatelessPayloadStatusV1>> ExecuteStatelessPayload(IExecutionPayloadParams executionPayloadParams, byte[] opaqueWitness, int version)
    {
        ExecutionPayload executionPayload = executionPayloadParams.ExecutionPayload;
        executionPayload.ExecutionRequests = executionPayloadParams.ExecutionRequests;
        StatelessPayloadStatusV1 invalidStatus = StatelessPayloadStatusV1.Invalid();

        if (!executionPayload.ValidateFork(_specProvider))
        {
            if (_logger.IsWarn) _logger.Warn("The payload is not supported by the current fork");
            return ResultWrapper<StatelessPayloadStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork, invalidStatus);
        }

        IReleaseSpec releaseSpec = _specProvider.GetSpec(executionPayload.BlockNumber, executionPayload.Timestamp);
        Nethermind.Merge.Plugin.Data.ValidationResult validationResult = executionPayloadParams.ValidateParams(releaseSpec, version, out string? error);
        if (validationResult != Nethermind.Merge.Plugin.Data.ValidationResult.Success)
        {
            if (_logger.IsWarn) _logger.Warn(error);
            return validationResult == Nethermind.Merge.Plugin.Data.ValidationResult.Fail
                ? ResultWrapper<StatelessPayloadStatusV1>.Fail(error!, ErrorCodes.InvalidParams, invalidStatus)
                : ResultWrapper<StatelessPayloadStatusV1>.Success(StatelessPayloadStatusV1.Invalid(error));
        }

        Block block;
        try
        {
            block = GetExecutionBlock(executionPayload, ignoreSuppliedBlockHash: true);
            RecoverTxSenders(block);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or RlpException)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid engine_executeStatelessPayloadV{version} params: {exception.Message}");
            return ResultWrapper<StatelessPayloadStatusV1>.Success(StatelessPayloadStatusV1.Invalid(exception.Message));
        }

        try
        {
            using Witness witness = DeserializeWitness(opaqueWitness, out ulong chainId);
            if (chainId != _specProvider.ChainId)
            {
                string chainError = $"Witness chain id {chainId} does not match execution chain id {_specProvider.ChainId}.";
                if (_logger.IsWarn) _logger.Warn(chainError);
                return ResultWrapper<StatelessPayloadStatusV1>.Success(StatelessPayloadStatusV1.Invalid(chainError));
            }

            Block processedBlock = StatelessExecutor.Execute(block, witness, _specProvider);
            return ResultWrapper<StatelessPayloadStatusV1>.Success(StatelessPayloadStatusV1.Valid(
                processedBlock.StateRoot ?? Keccak.Zero,
                processedBlock.ReceiptsRoot ?? Keccak.Zero));
        }
        catch (Exception exception) when (exception is InvalidBlockException or InvalidDataException or InvalidOperationException or ArgumentException or RlpException)
        {
            if (_logger.IsWarn) _logger.Warn($"engine_executeStatelessPayloadV{version} rejected payload: {exception.Message}");
            return ResultWrapper<StatelessPayloadStatusV1>.Success(StatelessPayloadStatusV1.Invalid(exception.Message));
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"engine_executeStatelessPayloadV{version} failed: {exception}");
            return ResultWrapper<StatelessPayloadStatusV1>.Fail(exception.Message);
        }
    }

    private static Witness DeserializeWitness(ReadOnlySpan<byte> opaqueWitness, out ulong chainId)
    {
        (_, Witness witness, chainId) = InputSerializer.Deserialize(opaqueWitness);
        return witness;
    }

    private Block GetExecutionBlock(ExecutionPayload executionPayload, bool ignoreSuppliedBlockHash)
    {
        BlockDecodingResult decodingResult = executionPayload.TryGetBlock();
        Block block = decodingResult.Block ?? throw new InvalidDataException(decodingResult.Error ?? "Payload could not be decoded as a block.");

        if (ignoreSuppliedBlockHash)
        {
            block.Header.Hash = block.Header.CalculateHash();
        }

        return block;
    }

    private void RecoverTxSenders(Block block)
    {
        EthereumEcdsa ecdsa = new(_specProvider.ChainId);
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        foreach (Transaction tx in block.Transactions)
        {
            tx.SenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
        }
    }
}
