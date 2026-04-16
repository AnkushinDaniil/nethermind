// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class BlockAccessListTests
{
    [Test]
    public void Rollback_current_index_removes_account_reads_from_skipped_transaction()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddAccountRead(TestItem.AddressA);
        blockAccessList.RollbackCurrentIndex();

        Assert.That(blockAccessList.AccountChanges, Is.Empty);
    }

    [Test]
    public void Rollback_current_index_removes_account_read_marker_from_existing_empty_entry()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddAccountChanges(new AccountChanges(TestItem.AddressA));
        blockAccessList.AddAccountRead(TestItem.AddressA);
        blockAccessList.RollbackCurrentIndex();
        blockAccessList.IncrementBlockAccessIndex();

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void Rollback_current_index_removes_account_read_after_reverted_value_transfer()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddBalanceChange(TestItem.AddressA, 0, 1);
        blockAccessList.AddAccountRead(TestItem.AddressA);
        blockAccessList.RollbackCurrentIndex();

        Assert.That(blockAccessList.AccountChanges, Is.Empty);
    }

    [Test]
    public void Rollback_current_index_removes_storage_reads_from_skipped_transaction()
    {
        UInt256 previousRead = 1;
        UInt256 skippedRead = 2;
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageRead(TestItem.AddressA, previousRead);
        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageRead(TestItem.AddressA, skippedRead);
        blockAccessList.AddAccountRead(TestItem.AddressB);
        blockAccessList.RollbackCurrentIndex();

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressB), Is.Null);
            Assert.That(accountChanges, Is.Not.Null);
            Assert.That(accountChanges!.StorageReads, Is.EqualTo(new[] { new StorageRead(previousRead) }));
        }
    }

    [Test]
    public void Rollback_current_index_removes_storage_reads_created_while_restoring_changes()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 1);
        blockAccessList.RollbackCurrentIndex();

        Assert.That(blockAccessList.AccountChanges.ToArray(), Is.Empty);
    }

    [Test]
    public void Rollback_current_index_preserves_prior_storage_read_removed_by_skipped_write()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageRead(TestItem.AddressA, 1);

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageRead(TestItem.AddressA, 1);
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 2);
        blockAccessList.RollbackCurrentIndex();

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges, Is.Not.Null);
            Assert.That(accountChanges!.StorageReads, Is.EqualTo(new[] { new StorageRead(1) }));
            Assert.That(accountChanges.StorageChanges, Is.Empty);
        }
    }

    [Test]
    public void Rollback_current_index_removes_current_storage_read_removed_by_skipped_write()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageRead(TestItem.AddressA, 1);
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 2);
        blockAccessList.RollbackCurrentIndex();

        Assert.That(blockAccessList.AccountChanges.ToArray(), Is.Empty);
    }

    [Test]
    public void Increment_index_prunes_empty_accounts_created_by_net_zero_balance_mutations()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddBalanceChange(TestItem.AddressA, 0, 1);
        blockAccessList.AddBalanceChange(TestItem.AddressA, 1, 0);
        blockAccessList.IncrementBlockAccessIndex();

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void Increment_index_preserves_explicit_account_reads()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddAccountRead(TestItem.AddressA);
        blockAccessList.IncrementBlockAccessIndex();

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
    }

    [Test]
    public void Prune_empty_accounts_removes_explicit_account_reads()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddAccountRead(TestItem.AddressA);
        blockAccessList.PruneEmptyAccounts();

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void Increment_index_prunes_transient_account_reads_without_state_changes()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddTransientAccountRead(TestItem.AddressA);
        blockAccessList.IncrementBlockAccessIndex();

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void Increment_index_prunes_empty_accounts_not_added_in_current_index()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.AddAccountChanges(new AccountChanges(TestItem.AddressA));
        blockAccessList.IncrementBlockAccessIndex();

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void No_op_storage_change_is_recorded_as_storage_read()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 2, 2);
        blockAccessList.IncrementBlockAccessIndex();

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges, Is.Not.Null);
            Assert.That(accountChanges!.StorageReads, Is.EqualTo(new[] { new StorageRead(1) }));
            Assert.That(accountChanges.StorageChanges, Is.Empty);
        }
    }

    [Test]
    public void Restore_after_selfdestruct_preserves_historical_indexed_changes()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 1);
        blockAccessList.AddNonceChange(TestItem.AddressA, 1);
        blockAccessList.AddCodeChange(TestItem.AddressA, [], new byte[] { 1 });

        blockAccessList.IncrementBlockAccessIndex();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 1, 2);
        blockAccessList.AddNonceChange(TestItem.AddressA, 2);
        blockAccessList.AddCodeChange(TestItem.AddressA, [1], new byte[] { 2 });

        int snapshot = blockAccessList.TakeSnapshot();
        blockAccessList.DeleteAccount(TestItem.AddressA, 0);
        blockAccessList.Restore(snapshot);

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges, Is.Not.Null);
            Assert.That(accountChanges!.StorageChanges.Single().Changes.Keys, Is.EqualTo(new ushort[] { 1, 2 }));
            Assert.That(accountChanges.NonceChanges.Select(c => c.BlockAccessIndex), Is.EqualTo(new ushort[] { 1, 2 }));
            Assert.That(accountChanges.CodeChanges.Select(c => c.BlockAccessIndex), Is.EqualTo(new ushort[] { 1, 2 }));
        }
    }

    [Test]
    public void Restore_removes_reverted_nonce_change_from_current_nonzero_index()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        int snapshot = blockAccessList.TakeSnapshot();
        blockAccessList.AddNonceChange(TestItem.AddressA, 1);
        blockAccessList.Restore(snapshot);

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA)?.NonceChanges, Is.Empty);
    }

    [Test]
    public void Restore_removes_reverted_code_change_from_current_nonzero_index()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        int snapshot = blockAccessList.TakeSnapshot();
        blockAccessList.AddCodeChange(TestItem.AddressA, [], new byte[] { 1 });
        blockAccessList.Restore(snapshot);

        Assert.That(blockAccessList.GetAccountChanges(TestItem.AddressA)?.CodeChanges, Is.Empty);
    }

    [Test]
    public void Restore_converts_reverted_storage_write_to_storage_read()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        int snapshot = blockAccessList.TakeSnapshot();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 2);
        blockAccessList.Restore(snapshot);

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges, Is.Not.Null);
            Assert.That(accountChanges!.StorageReads, Is.EqualTo(new[] { new StorageRead(1) }));
            Assert.That(accountChanges.StorageChanges, Is.Empty);
        }
    }

    [Test]
    public void Restore_converts_multiple_reverted_storage_writes_to_storage_reads()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        int snapshot = blockAccessList.TakeSnapshot();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 2);
        blockAccessList.AddStorageChange(TestItem.AddressA, 2, 0, 3);
        blockAccessList.Restore(snapshot);

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(accountChanges?.StorageReads, Is.EqualTo(new[] { new StorageRead(1), new StorageRead(2) }));
    }

    [Test]
    public void Restore_converts_repeated_reverted_storage_write_to_single_storage_read()
    {
        BlockAccessList blockAccessList = new();

        blockAccessList.IncrementBlockAccessIndex();
        int snapshot = blockAccessList.TakeSnapshot();
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 0, 2);
        blockAccessList.AddStorageChange(TestItem.AddressA, 1, 2, 3);
        blockAccessList.Restore(snapshot);

        AccountChanges? accountChanges = blockAccessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(accountChanges?.StorageReads, Is.EqualTo(new[] { new StorageRead(1) }));
    }
}
