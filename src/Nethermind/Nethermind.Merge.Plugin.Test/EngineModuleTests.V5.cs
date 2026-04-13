// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CkzgLib;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task GetPayloadV5_should_return_all_the_blobs([Values(0, 1, 2, 3, 4)] int blobTxCount, [Values(true, false)] bool oneBlobPerTx)
    {
        (IEngineRpcModule rpcModule, string? payloadId, _, _) = await BuildAndGetPayloadV3Result(Osaka.Instance, blobTxCount, oneBlobPerTx: oneBlobPerTx);
        ResultWrapper<GetPayloadV5Result?> result = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));
        BlobsBundleV2 getPayloadResultBlobsBundle = result.Data!.BlobsBundle!;
        Assert.That(result.Data.ExecutionPayload.BlobGasUsed, Is.EqualTo(BlobGasCalculator.CalculateBlobGas(blobTxCount)));
        Assert.That(getPayloadResultBlobsBundle.Blobs!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Commitments!.Length, Is.EqualTo(blobTxCount));
        Assert.That(getPayloadResultBlobsBundle.Proofs!.Length, Is.EqualTo(blobTxCount * Ckzg.CellsPerExtBlob));
        ShardBlobNetworkWrapper wrapper = new ShardBlobNetworkWrapper(getPayloadResultBlobsBundle.Blobs,
            getPayloadResultBlobsBundle.Commitments, getPayloadResultBlobsBundle.Proofs, ProofVersion.V1);
        Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(wrapper), Is.True);
    }

    [Test]
    public async Task NewPayloadV5_should_accept_osaka_payload_without_bal()
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] transactions, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Osaka.Instance, 1);
        using (chain)
        {
            ResultWrapper<GetPayloadV5Result?> payloadResult = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));

            Assert.That(payloadResult.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(payloadResult.Data, Is.Not.Null);

            byte[]?[] blobVersionedHashes = transactions.SelectMany(static tx => tx.BlobVersionedHashes ?? []).ToArray();
            ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV5(
                payloadResult.Data!.ExecutionPayload,
                blobVersionedHashes,
                payloadResult.Data.ExecutionPayload.ParentBeaconBlockRoot,
                payloadResult.Data.ExecutionRequests ?? []);

            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(payloadResult.Data.ExecutionPayload.BlockHash));
        }
    }

    [Test]
    public async Task NewPayloadV5_should_accept_amsterdam_payload_without_bal_or_slot_number()
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] transactions, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Amsterdam.Instance, 1);
        using (chain)
        {
            ResultWrapper<GetPayloadV5Result?> payloadResult = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));

            Assert.That(payloadResult.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(payloadResult.Data, Is.Not.Null);
            Assert.That(payloadResult.Data!.ExecutionPayload.BlockAccessList, Is.Null);
            Assert.That(payloadResult.Data.ExecutionPayload.SlotNumber, Is.Null);

            byte[]?[] blobVersionedHashes = transactions.SelectMany(static tx => tx.BlobVersionedHashes ?? []).ToArray();
            ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV5(
                payloadResult.Data.ExecutionPayload,
                blobVersionedHashes,
                payloadResult.Data.ExecutionPayload.ParentBeaconBlockRoot,
                payloadResult.Data.ExecutionRequests ?? []);

            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(payloadResult.Data.ExecutionPayload.BlockHash));
        }
    }

    [Test]
    public async Task NewPayloadWithWitnessV5_should_round_trip_into_executeStatelessPayloadV5()
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] transactions, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Osaka.Instance, 1);
        using (chain)
        {
            ResultWrapper<GetPayloadV5Result?> payloadResult = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));

            payloadResult.Result.Should().Be(Result.Success);
            payloadResult.Data.Should().NotBeNull();

            byte[]?[] blobVersionedHashes = transactions.SelectMany(static tx => tx.BlobVersionedHashes ?? []).ToArray();
            ResultWrapper<PayloadStatusV1> witnessResult = await rpcModule.engine_newPayloadWithWitnessV5(
                payloadResult.Data!.ExecutionPayload,
                blobVersionedHashes,
                payloadResult.Data.ExecutionPayload.ParentBeaconBlockRoot,
                payloadResult.Data.ExecutionRequests ?? []);

            witnessResult.Result.Should().Be(Result.Success);
            witnessResult.Data.Status.Should().Be(PayloadStatus.Valid);
            witnessResult.Data.LatestValidHash.Should().Be(payloadResult.Data.ExecutionPayload.BlockHash);
            witnessResult.Data.Witness.Should().NotBeNullOrEmpty();

            ResultWrapper<StatelessPayloadStatusV1> statelessResult = await rpcModule.engine_executeStatelessPayloadV5(
                payloadResult.Data.ExecutionPayload,
                blobVersionedHashes,
                payloadResult.Data.ExecutionPayload.ParentBeaconBlockRoot,
                payloadResult.Data.ExecutionRequests ?? [],
                witnessResult.Data.Witness!);

            statelessResult.Result.Should().Be(Result.Success);
            statelessResult.Data.Status.Should().Be(PayloadStatus.Valid);
            statelessResult.Data.StateRoot.Should().Be(payloadResult.Data.ExecutionPayload.StateRoot);
            statelessResult.Data.ReceiptsRoot.Should().Be(payloadResult.Data.ExecutionPayload.ReceiptsRoot);
            statelessResult.Data.ValidationError.Should().BeNull();
        }
    }

    [Test]
    public async Task WitnessV5_methods_should_round_trip_over_rpc()
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] transactions, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Osaka.Instance, 1);
        using (chain)
        {
            ResultWrapper<GetPayloadV5Result?> payloadResult = await rpcModule.engine_getPayloadV5(Bytes.FromHexString(payloadId!));
            payloadResult.Result.Should().Be(Result.Success);
            payloadResult.Data.Should().NotBeNull();

            byte[]?[] blobVersionedHashes = transactions.SelectMany(static tx => tx.BlobVersionedHashes ?? []).ToArray();

            string newPayloadResponse = await RpcTest.TestSerializedRequest(
                rpcModule,
                nameof(IEngineRpcModule.engine_newPayloadWithWitnessV5),
                payloadResult.Data!.ExecutionPayload,
                blobVersionedHashes,
                payloadResult.Data.ExecutionPayload.ParentBeaconBlockRoot,
                payloadResult.Data.ExecutionRequests ?? []);

            using JsonDocument newPayloadJson = JsonDocument.Parse(newPayloadResponse);
            JsonElement newPayloadResult = newPayloadJson.RootElement.GetProperty("result");
            newPayloadResult.GetProperty("status").GetString().Should().Be(PayloadStatus.Valid);
            string? witnessHex = newPayloadResult.GetProperty("witness").GetString();
            witnessHex.Should().NotBeNullOrEmpty();

            string statelessResponse = await RpcTest.TestSerializedRequest(
                rpcModule,
                nameof(IEngineRpcModule.engine_executeStatelessPayloadV5),
                payloadResult.Data.ExecutionPayload,
                blobVersionedHashes,
                payloadResult.Data.ExecutionPayload.ParentBeaconBlockRoot,
                payloadResult.Data.ExecutionRequests ?? [],
                Bytes.FromHexString(witnessHex!));

            using JsonDocument statelessJson = JsonDocument.Parse(statelessResponse);
            JsonElement statelessResult = statelessJson.RootElement.GetProperty("result");
            statelessResult.GetProperty("status").GetString().Should().Be(PayloadStatus.Valid);
            statelessResult.GetProperty("stateRoot").GetString().Should().Be(payloadResult.Data.ExecutionPayload.StateRoot.ToString(true));
            statelessResult.GetProperty("receiptsRoot").GetString().Should().Be(payloadResult.Data.ExecutionPayload.ReceiptsRoot.ToString(true));
        }
    }

    [Test]
    public async Task GetBlobsV2_should_throw_if_more_than_128_requested_blobs([Values(128, 129)] int requestSize)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        List<byte[]> request = new List<byte[]>(requestSize);
        for (int i = 0; i < requestSize; i++)
        {
            request.Add(Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IEnumerable<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(request.ToArray());

        if (requestSize > 128)
        {
            result.Result.Should().BeEquivalentTo(Result.Fail($"The number of requested blobs must not exceed 128"));
            result.ErrorCode.Should().Be(MergeErrorCodes.TooLargeRequest);
        }
        else
        {
            result.Result.Should().Be(Result.Success);
            result.Data.Should().BeNull();
        }
    }

    [Test]
    public async Task GetBlobsV2_should_handle_empty_request()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        ResultWrapper<IEnumerable<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2([]);

        result.Result.Should().Be(Result.Success);
        result.Data.Should().BeEquivalentTo(ArraySegment<BlobAndProofV2>.Empty);
    }

    [Test]
    public async Task GetBlobsV2_should_return_requested_blobs([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfBlobs, spec: Osaka.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

        ResultWrapper<IEnumerable<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobTx.BlobVersionedHashes!);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

        result.Data.Should().NotBeNull();
        result.Data!.Select(static b => b!.Blob).Should().BeEquivalentTo(wrapper.Blobs);
        result.Data!.Select(static b => b!.Proofs.Length).Should().HaveCount(numberOfBlobs);
        result.Data!.Select(static b => b!.Proofs).Should().BeEquivalentTo(wrapper.Proofs.Chunk(128));
    }

    [Test]
    public async Task GetBlobsV2_should_return_empty_array_when_blobs_not_found([Values(1, 2, 3, 4, 5, 6)] int numberOfRequestedBlobs)
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        // we are not adding this tx
        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfRequestedBlobs)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        // requesting hashes that are not present in TxPool
        ResultWrapper<IEnumerable<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobTx.BlobVersionedHashes!);

        result.Result.Should().Be(Result.Success);
        result.Data.Should().BeNull();
    }

    [Test]
    public async Task GetBlobsV2_should_return_empty_array_when_only_some_blobs_found([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs, [Values(1, 2)] int multiplier)
    {
        int requestSize = multiplier * numberOfBlobs;

        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfBlobs, spec: Osaka.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

        List<byte[]> blobVersionedHashesRequest = new List<byte[]>(requestSize);

        int actualIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool addActualHash = i % multiplier == 0;
            blobVersionedHashesRequest.Add(addActualHash ? blobTx.BlobVersionedHashes![actualIndex++]! : Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IEnumerable<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV2(blobVersionedHashesRequest.ToArray());
        if (multiplier > 1)
        {
            result.Result.Should().Be(Result.Success);
            result.Data.Should().BeNull();
        }
        else
        {
            ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

            result.Data.Should().NotBeNull();
            result.Data!.Select(static b => b!.Blob).Should().BeEquivalentTo(wrapper.Blobs);
            result.Data!.Select(static b => b!.Proofs.Length).Should().HaveCount(numberOfBlobs);
            result.Data!.Select(static b => b!.Proofs).Should().BeEquivalentTo(wrapper.Proofs.Chunk(128));
        }
    }

    [Test]
    public async Task GetBlobsV3_should_return_partial_results_with_nulls_for_missing_blobs([Values(1, 2, 3, 4, 5, 6)] int numberOfBlobs, [Values(1, 2)] int multiplier)
    {
        int requestSize = multiplier * numberOfBlobs;

        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance, mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromDays(1).TotalMilliseconds
        });
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(numberOfBlobs, spec: Osaka.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(1000.Wei)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        chain.TxPool.SubmitTx(blobTx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);

        List<byte[]> blobVersionedHashesRequest = new List<byte[]>(requestSize);

        int actualIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool addActualHash = i % multiplier == 0;
            blobVersionedHashesRequest.Add(addActualHash ? blobTx.BlobVersionedHashes![actualIndex++]! : Bytes.FromHexString(i.ToString("X64")));
        }

        ResultWrapper<IEnumerable<BlobAndProofV2?>?> result = await rpcModule.engine_getBlobsV3(blobVersionedHashesRequest.ToArray());

        result.Result.Should().Be(Result.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Should().HaveCount(requestSize);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)blobTx.NetworkWrapper!;

        // V3 returns partial results with nulls for missing blobs
        int foundIndex = 0;
        for (int i = 0; i < requestSize; i++)
        {
            bool shouldBeFound = i % multiplier == 0;
            if (shouldBeFound)
            {
                result.Data!.ElementAt(i).Should().NotBeNull();
                result.Data!.ElementAt(i)!.Blob.Should().BeEquivalentTo(wrapper.Blobs[foundIndex]);
                result.Data!.ElementAt(i)!.Proofs.Should().BeEquivalentTo(wrapper.Proofs.Skip(foundIndex * 128).Take(128));
                foundIndex++;
            }
            else
            {
                result.Data!.ElementAt(i).Should().BeNull();
            }
        }
    }

    [Test]
    public async Task GetBlobsV1_should_return_invalid_fork_post_osaka()
    {
        MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Osaka.Instance);
        IEngineRpcModule rpcModule = chain.EngineRpcModule;

        ResultWrapper<IEnumerable<BlobAndProofV1?>> result = await rpcModule.engine_getBlobsV1([]);

        result.Result.Should().BeEquivalentTo(Result.Fail(MergeErrorMessages.UnsupportedFork));
        result.ErrorCode.Should().Be(MergeErrorCodes.UnsupportedFork);
    }

    [Test]
    public async Task BlobsV2DirectResponse_WriteToAsync_produces_valid_json()
    {
        // Build a small list with one real entry and one null
        byte[] blob = new byte[16];
        Random.Shared.NextBytes(blob);
        byte[] proof1 = new byte[48];
        Random.Shared.NextBytes(proof1);
        byte[] proof2 = new byte[48];
        Random.Shared.NextBytes(proof2);

        byte[]?[] blobs = [blob, null];
        ReadOnlyMemory<byte[]>[] proofs = [new ReadOnlyMemory<byte[]>([proof1, proof2]), default];

        BlobsV2DirectResponse response = new(blobs, proofs, 2);

        // Write via streaming path
        Pipe pipe = new();
        await response.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string streamedJson = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        // Write via STJ for comparison
        string stjJson = JsonSerializer.Serialize(response, EthereumJsonSerializer.JsonOptions);

        streamedJson.Should().Be(stjJson);
    }

    [Test]
    public async Task BlobsV2DirectResponse_WriteToAsync_empty_list()
    {
        BlobsV2DirectResponse response = new([], [], 0);

        Pipe pipe = new();
        await response.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string json = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        json.Should().Be("[]");
    }

    [Test]
    public void BlobsV2DirectResponse_WriteToAsync_throws_on_cancelled_token()
    {
        byte[] blob = new byte[131072]; // 128KB
        byte[]?[] blobs = [blob];
        ReadOnlyMemory<byte[]>[] proofs = [new ReadOnlyMemory<byte[]>([new byte[48]])];
        BlobsV2DirectResponse response = new(blobs, proofs, 1);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Pipe pipe = new();
        Func<Task> act = async () => await response.WriteToAsync(pipe.Writer, cts.Token);
        act.Should().ThrowAsync<OperationCanceledException>();
    }
}
