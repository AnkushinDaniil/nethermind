// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockProcessorTests
{
    private static (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider) CreateProcessorAndBranch(
        IRewardCalculator? rewardCalculator = null,
        IBlockCachePreWarmer? preWarmer = null,
        ISpecProvider? specProvider = null,
        IWorldState? stateProvider = null)
    {
        specProvider ??= HoodiSpecProvider.Instance;
        stateProvider ??= TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(specProvider,
            TestBlockValidator.AlwaysValid,
            rewardCalculator ?? NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));
        BranchProcessor branchProcessor = new(
            processor,
            specProvider,
            stateProvider,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            preWarmer);

        return (processor, branchProcessor, stateProvider);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Prepared_block_contains_author_field()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch();

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            NullBlockTracer.Instance);
        Assert.That(processedBlocks.Length, Is.EqualTo(1), "length");
        Assert.That(processedBlocks[0].Author, Is.EqualTo(block.Author), "author");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Recovers_state_on_cancel()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(
            rewardCalculator: new RewardCalculator(MainnetSpecProvider.Instance));

        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithTransactions(1, MuirGlacier.Instance).WithHeader(header).TestObject;
        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            AlwaysCancelBlockTracer.Instance));

        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            AlwaysCancelBlockTracer.Instance));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(20)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(129)]
    [TestCase(130)]
    [TestCase(1000)]
    [TestCase(2000)]
    public async Task Process_long_running_branch(int blocksAmount)
    {
        Address address = TestItem.Addresses[0];
        TestSingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance);
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(spec);
        testRpc.TestWallet.UnlockAccount(address, new SecureString());
        await testRpc.AddFunds(address, 1.Ether);
        await testRpc.AddBlock();
        SemaphoreSlim suggestedBlockResetEvent = new SemaphoreSlim(0);
        testRpc.BlockTree.NewHeadBlock += (_, _) =>
        {
            suggestedBlockResetEvent.Release(1);
        };

        int branchLength = blocksAmount + (int)testRpc.BlockTree.BestKnownNumber + 1;
        ((BlockTree)testRpc.BlockTree).AddBranch(branchLength, (int)testRpc.BlockTree.BestKnownNumber);
        Assert.That(await suggestedBlockResetEvent.WaitAsync(TestBlockchain.DefaultTimeout * 10), Is.True);
        Assert.That((int)testRpc.BlockTree.BestKnownNumber, Is.EqualTo(branchLength - 1));
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TransactionsExecuted_event_fires_during_ProcessOne()
    {
        (BlockProcessor processor, _, IWorldState stateProvider) = CreateProcessorAndBranch();

        bool eventFired = false;
        processor.TransactionsExecuted += () => eventFired = true;

        using IDisposable scope = stateProvider.BeginScope(null);
        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block.Header);

        processor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(eventFired, Is.True, "TransactionsExecuted should fire after ProcessTransactions completes");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_cancels_prewarmer_via_TransactionsExecuted_event()
    {
        TokenCapturingPreWarmer preWarmer = new();
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(preWarmer: preWarmer);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        Assert.That(preWarmer.CapturedToken.IsCancellationRequested, Is.True,
            "prewarmer CancellationToken should be cancelled via TransactionsExecuted event after tx processing");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_unsubscribes_from_TransactionsExecuted_after_processing()
    {
        (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider) = CreateProcessorAndBranch();

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        // After Process returns, the event handler should be unsubscribed.
        // Verify by checking that firing the event doesn't cause issues
        // (if still subscribed, it would try to cancel a disposed CTS).
        int externalHandlerCallCount = 0;
        processor.TransactionsExecuted += () => externalHandlerCallCount++;

        // Process another block to trigger the event — only our handler should fire
        using IDisposable scope = stateProvider.BeginScope(null);
        Block block2 = Build.A.Block.WithHeader(Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block2.Header);
        processor.ProcessOne(block2, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(externalHandlerCallCount, Is.EqualTo(1),
            "only the externally subscribed handler should fire, BranchProcessor should have unsubscribed");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [TestCaseSource(nameof(BlockValidationTransactionsExecutor_bal_validation_cases))]
    public void BlockValidationTransactionsExecutor_validates_bal_only_when_validation_enabled(
        ProcessingOptions processingOptions,
        bool shouldValidateBlockAccessList)
    {
        const long gasLimit = 100_000L;
        const long txBlockGasUsed = 37_568L;
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.LoadSuggestedBlockAccessList(new BlockAccessList(), txBlockGasUsed);

        ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();
        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(static callInfo =>
        {
            Transaction transaction = callInfo.Arg<Transaction>();
            transaction.SpentGas = 63_586;
            transaction.BlockGasUsed = txBlockGasUsed;
            return TransactionResult.Ok;
        });

        BlockProcessor.BlockValidationTransactionsExecutor txExecutor = new(transactionProcessor, stateProvider);
        Block block = Build.A.Block.WithGasLimit(gasLimit).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        txExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, CancellationToken.None);

        Assert.That(stateProvider.ValidatedGasRemaining, Is.Empty);
    }

    [TestCase(2_000, false)]
    [TestCase(1_999, true)]
    [MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_read_budget_uses_eip_7928_item_cost(long gasRemaining, bool shouldThrow)
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddStorageRead(TestItem.AddressA, 1);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, gasRemaining);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, gasRemaining);

        if (shouldThrow)
        {
            Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
        }
        else
        {
            Assert.That(act, Throws.Nothing);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_allows_surplus_account_only_reads()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddAccountRead(TestItem.AddressA);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, 10_000);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 1, 10_000);

        Assert.That(act, Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_allows_surplus_index_zero_account_only_reads()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddAccountRead(TestItem.AddressA);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, 10_000);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, 10_000);

        Assert.That(act, Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_rejects_missing_account_only_reads()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.LoadSuggestedBlockAccessList(new BlockAccessList(), 10_000);
        stateProvider.GeneratedBlockAccessList.AddAccountRead(TestItem.AddressA);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, 10_000);

        Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_rejects_surplus_balance_changes()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddBalanceChange(TestItem.AddressA, 0, 1);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, 10_000);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, 10_000);

        Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_allows_surplus_request_contract_reads()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddStorageRead(Eip7002Constants.WithdrawalRequestPredeployAddress, 0);
        suggestedBlockAccessList.AddStorageRead(Eip7251Constants.ConsolidationRequestPredeployAddress, 0);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, 0);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, 0);

        Assert.That(act, Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_records_original_storage_reads_in_bal()
    {
        ParallelWorldState stateProvider = new(Substitute.For<IWorldState>())
        {
            TracingEnabled = true
        };
        StorageCell storageCell = new(TestItem.AddressA, 1);

        stateProvider.GetOriginal(storageCell);

        AccountChanges? accountChanges = stateProvider.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(accountChanges?.StorageReads, Does.Contain(new StorageRead(1)));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_records_zero_balance_mutator_account_reads_in_bal()
    {
        ParallelWorldState stateProvider = new(Substitute.For<IWorldState>())
        {
            TracingEnabled = true
        };

        stateProvider.AddToBalanceAndCreateIfNotExists(TestItem.AddressA, UInt256.Zero, Amsterdam.Instance, out _);

        Assert.That(stateProvider.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_set_bal_uses_suggested_bal_hash_when_present()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.GeneratedBlockAccessList.AddAccountRead(TestItem.AddressA);
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddAccountRead(TestItem.AddressB);
        byte[] expectedEncodedBal = Rlp.Encode(suggestedBlockAccessList).Bytes;
        Hash256 expectedBalHash = new(ValueKeccak.Compute(expectedEncodedBal).Bytes);
        Block block = new(Build.A.BlockHeader.WithNumber(1).TestObject, [], [], blockAccessList: suggestedBlockAccessList);

        stateProvider.SetBlockAccessList(block, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.BlockAccessList, Is.SameAs(suggestedBlockAccessList));
            Assert.That(block.GeneratedBlockAccessList, Is.SameAs(stateProvider.GeneratedBlockAccessList));
            Assert.That(block.EncodedBlockAccessList, Is.EqualTo(expectedEncodedBal));
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(expectedBalHash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_set_bal_preserves_existing_encoded_suggested_bal()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.GeneratedBlockAccessList.AddAccountRead(TestItem.AddressA);
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddAccountRead(TestItem.AddressB);
        byte[] expectedEncodedBal = [0xc0];
        Hash256 expectedBalHash = new(ValueKeccak.Compute(expectedEncodedBal).Bytes);
        Block block = new(Build.A.BlockHeader.WithNumber(1).TestObject, [], [], blockAccessList: suggestedBlockAccessList)
        {
            EncodedBlockAccessList = expectedEncodedBal
        };

        stateProvider.SetBlockAccessList(block, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.BlockAccessList, Is.SameAs(suggestedBlockAccessList));
            Assert.That(block.GeneratedBlockAccessList, Is.SameAs(stateProvider.GeneratedBlockAccessList));
            Assert.That(block.EncodedBlockAccessList, Is.SameAs(expectedEncodedBal));
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(expectedBalHash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_set_bal_reencodes_generated_bal_after_preserving_account_reads()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.TracingEnabled = true;
        stateProvider.GeneratedBlockAccessList.IncrementBlockAccessIndex();
        stateProvider.GeneratedBlockAccessList.AddAccountRead(TestItem.AddressA);
        stateProvider.AddAccountRead(TestItem.AddressB);
        BlockAccessList expectedBal = new();
        expectedBal.AddAccountRead(TestItem.AddressA);
        expectedBal.AddAccountRead(TestItem.AddressB);
        byte[] expectedEncodedBal = Rlp.Encode(expectedBal).Bytes;
        Hash256 expectedBalHash = new(ValueKeccak.Compute(expectedEncodedBal).Bytes);
        Block block = new(Build.A.BlockHeader.WithNumber(1).TestObject, [], [])
        {
            EncodedBlockAccessList = [0xfa]
        };

        stateProvider.SetBlockAccessList(block, Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.BlockAccessList!.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
            Assert.That(block.BlockAccessList.GetAccountChanges(TestItem.AddressB), Is.Not.Null);
            Assert.That(block.GeneratedBlockAccessList, Is.SameAs(stateProvider.GeneratedBlockAccessList));
            Assert.That(block.EncodedBlockAccessList, Is.EqualTo(expectedEncodedBal));
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(expectedBalHash));
            Assert.That(stateProvider.TracingEnabled, Is.False);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Processed_block_preserves_encoded_block_access_list_from_suggested_block()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch();
        byte[] encodedBal = [0xc0];
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject)
            .WithBlockAccessList(new BlockAccessList())
            .WithEncodedBlockAccessList(encodedBal)
            .TestObject;

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        Assert.That(processedBlocks[0].EncodedBlockAccessList, Is.SameAs(encodedBal));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Produced_block_does_not_preserve_stale_block_access_list_from_previous_payload()
    {
        Nethermind.Specs.Test.OverridableReleaseSpec spec = new(Prague.Instance)
        {
            IsEip7928Enabled = true,
            IsEip6110Enabled = false,
            IsEip7002Enabled = false,
            IsEip7251Enabled = false,
        };
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(specProvider: new TestSingleReleaseSpecProvider(spec));
        BlockAccessList staleBal = new();
        staleBal.AddAccountRead(TestItem.AddressA);
        byte[] staleEncodedBal = [0xfa];
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject)
        {
            BlockAccessList = staleBal,
            EncodedBlockAccessList = staleEncodedBal
        };

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.ProducingBlock,
            NullBlockTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processedBlocks[0].BlockAccessList!.GetAccountChanges(TestItem.AddressA), Is.Null);
            Assert.That(processedBlocks[0].EncodedBlockAccessList, Is.Not.SameAs(staleEncodedBal));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Produced_block_forces_generated_bal_when_processing_on_main_state()
    {
        Nethermind.Specs.Test.OverridableReleaseSpec spec = new(Prague.Instance)
        {
            IsEip7928Enabled = true,
            IsEip6110Enabled = false,
            IsEip7002Enabled = false,
            IsEip7251Enabled = false,
        };
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(
            specProvider: new TestSingleReleaseSpecProvider(spec),
            stateProvider: stateProvider);
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject)
        {
            BlockAccessList = new BlockAccessList(),
            EncodedBlockAccessList = [0xfa]
        };

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation | ProcessingOptions.StoreReceipts | ProcessingOptions.DoNotUpdateHead,
            NullBlockTracer.Instance);

        Assert.That(stateProvider.SetBlockAccessListForceGenerated, Is.EqualTo(new[] { true }));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ProducingBlock_option_regenerates_bal_for_regular_block_with_stale_payload_bal()
    {
        Nethermind.Specs.Test.OverridableReleaseSpec spec = new(Prague.Instance)
        {
            IsEip7928Enabled = true,
            IsEip6110Enabled = false,
            IsEip7002Enabled = false,
            IsEip7251Enabled = false,
        };
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(specProvider: new TestSingleReleaseSpecProvider(spec));
        BlockAccessList staleBal = new();
        staleBal.AddAccountRead(TestItem.AddressA);
        byte[] staleEncodedBal = [0xfa];
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject)
            .WithBlockAccessList(staleBal)
            .WithEncodedBlockAccessList(staleEncodedBal)
            .TestObject;

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.ProducingBlock,
            NullBlockTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processedBlocks[0].BlockAccessList!.GetAccountChanges(TestItem.AddressA), Is.Null);
            Assert.That(processedBlocks[0].EncodedBlockAccessList, Is.Not.SameAs(staleEncodedBal));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_no_prewarmer_still_processes_successfully()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(preWarmer: null);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        Assert.That(processedBlocks, Has.Length.EqualTo(1), "block should process successfully without a prewarmer");
    }

    [Test]
    public void NullBlockProcessor_TransactionsExecuted_subscribe_unsubscribe_is_safe()
    {
        IBlockProcessor processor = NullBlockProcessor.Instance;

        // Should not throw
        Action handler = () => { };
        processor.TransactionsExecuted += handler;
        processor.TransactionsExecuted -= handler;
    }

    [Test]
    public void BlockProductionTransactionPicker_validates_block_length_using_proper_tx_form()
    {
        IReleaseSpec spec = Osaka.Instance;
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(spec);

        Transaction transactionWithNetworkForm = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1, true, spec)
            .SignedAndResolved()
            .TestObject;

        BlockProcessor.BlockProductionTransactionPicker txPicker = new(specProvider, transactionWithNetworkForm.GetLength(true) / 1.KiB - 1);
        BlockToProduce newBlock = new(Build.A.BlockHeader.WithExcessBlobGas(0).TestObject);
        IWorldState stateProvider = new WorldStateStab();

        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction? addedTransaction = null;
        txPicker.AddingTransaction += (s, e) => addedTransaction = e.Transaction;

        txPicker.CanAddTransaction(newBlock, transactionWithNetworkForm, new HashSet<Transaction>(), stateProvider.GetUntrackedReader());

        Assert.That(addedTransaction, Is.EqualTo(transactionWithNetworkForm));
    }

    /// <summary>
    /// Manual IBlockCachePreWarmer that captures the CancellationToken for test verification.
    /// NSubstitute cannot proxy ReadOnlySpan&lt;T&gt; (ref struct) parameters.
    /// </summary>
    private class TokenCapturingPreWarmer : IBlockCachePreWarmer
    {
        public CancellationToken CapturedToken { get; private set; }

        public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec,
            CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
        {
            CapturedToken = cancellationToken;
            return Task.CompletedTask;
        }

        public CacheType ClearCaches() => default;
        public void Dispose() { }
    }

    private sealed class TrackingBlockAccessListWorldState(IWorldState innerWorldState)
        : WrappedWorldState(innerWorldState), IBlockAccessListBuilder
    {
        public bool TracingEnabled { get; set; }
        public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
        public List<long> ValidatedGasRemaining { get; } = [];
        public List<bool> SetBlockAccessListForceGenerated { get; } = [];

        private long _gasUsed;

        public void AddAccountRead(Address address)
        {
        }

        public void AddStorageRead(in StorageCell storageCell)
        {
        }

        public void LoadSuggestedBlockAccessList(BlockAccessList suggested, long gasUsed) => _gasUsed = gasUsed;

        public long GasUsed()
            => _gasUsed;

        public void ValidateBlockAccessList(BlockHeader block, ushort index, long gasRemaining)
            => ValidatedGasRemaining.Add(gasRemaining);

        public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        {
        }

        public override void RecalculateStateRoot()
        {
        }

        public void SetBlockAccessList(Block block, IReleaseSpec spec, bool forceGenerated = false)
            => SetBlockAccessListForceGenerated.Add(forceGenerated);
    }

    public static IEnumerable<TestCaseData> BlockValidationTransactionsExecutor_bal_validation_cases()
    {
        yield return new TestCaseData(ProcessingOptions.None, true)
            .SetName("BlockValidationTransactionsExecutor_defers_bal_validation_until_block_post_processing");
        yield return new TestCaseData(ProcessingOptions.NoValidation, false)
            .SetName("BlockValidationTransactionsExecutor_skips_bal_validation_when_no_validation_requested");
    }
}
