// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public class BlockAccessList : IEquatable<BlockAccessList>, IJournal<int>
{
    [JsonIgnore]
    public ushort Index = 0;
    public IEnumerable<AccountChanges> AccountChanges => _accountChanges.Values;
    private readonly SortedDictionary<Address, AccountChanges> _accountChanges;
    private readonly Stack<Change> _changes;
    private readonly Stack<Address> _accountsAddedInCurrentIndex;
    private readonly Stack<StorageCell> _storageReadsAddedInCurrentIndex;
    private readonly Stack<Address> _accountReadsAddedInCurrentIndex;
    private readonly HashSet<Address> _accountReads;

    public BlockAccessList()
    {
        _accountChanges = [];
        _changes = new();
        _accountsAddedInCurrentIndex = new();
        _storageReadsAddedInCurrentIndex = new();
        _accountReadsAddedInCurrentIndex = new();
        _accountReads = [];
    }

    public BlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges)
    {
        _accountChanges = accountChanges;
        _changes = new();
        _accountsAddedInCurrentIndex = new();
        _storageReadsAddedInCurrentIndex = new();
        _accountReadsAddedInCurrentIndex = new();
        _accountReads = [];
        foreach (KeyValuePair<Address, AccountChanges> item in accountChanges)
        {
            if (IsEmpty(item.Value))
            {
                _accountReads.Add(item.Key);
            }
        }
    }

    public bool Equals(BlockAccessList? other)
    {
        if (other is null)
            return false;

        if (_accountChanges.Count != other._accountChanges.Count)
            return false;

        foreach (KeyValuePair<Address, AccountChanges> pair in _accountChanges)
        {
            if (!other._accountChanges.TryGetValue(pair.Key, out AccountChanges? otherValue))
                return false;

            if (pair.Value != otherValue)
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) =>
        obj is BlockAccessList other && Equals(other);

    public override int GetHashCode() =>
        _accountChanges.Count.GetHashCode();

    public static bool operator ==(BlockAccessList left, BlockAccessList right) =>
        left.Equals(right);

    public static bool operator !=(BlockAccessList left, BlockAccessList right) =>
        !(left == right);

    public AccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out AccountChanges? value) ? value : null;

    public int ItemCount()
    {
        int count = _accountChanges.Count;
        foreach (AccountChanges accountChanges in _accountChanges.Values)
            count += accountChanges.StorageChanges.Count + accountChanges.StorageReads.Count;
        return count;
    }

    public void IncrementBlockAccessIndex()
    {
        PruneEmptyNonReadAccounts();
        _changes.Clear();
        _accountsAddedInCurrentIndex.Clear();
        _storageReadsAddedInCurrentIndex.Clear();
        _accountReadsAddedInCurrentIndex.Clear();
        Index++;
    }

    public void RollbackCurrentIndex()
    {
        Restore(0);
        RollbackReadsAddedInCurrentIndex();
        _changes.Clear();
        Index--;
    }

    public void Clear()
    {
        _accountChanges.Clear();
        _changes.Clear();
        _accountsAddedInCurrentIndex.Clear();
        _storageReadsAddedInCurrentIndex.Clear();
        _accountReadsAddedInCurrentIndex.Clear();
        _accountReads.Clear();
        Index = 0;
    }

    public void AddBalanceChange(Address address, UInt256 before, UInt256 after)
    {
        bool isZeroBalanceChange = before == after;
        if (address == Address.SystemUser && isZeroBalanceChange)
        {
            return;
        }

        if (isZeroBalanceChange)
        {
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        bool changedDuringTx = HasBalanceChangedDuringTx(address, before, after);
        accountChanges.PopBalanceChange(Index, out BalanceChange? oldBalanceChange);

        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.BalanceChange,
            PreviousValue = oldBalanceChange,
            PreTxBalance = before,
            BlockAccessIndex = Index
        });

        if (changedDuringTx)
        {
            accountChanges.AddBalanceChange(new(Index, after));
        }
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        bool changedDuringTx = HasCodeChangedDuringTx(accountChanges.Address, before, after.Span);
        accountChanges.PopCodeChange(Index, out CodeChange? oldCodeChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.CodeChange,
            PreviousValue = oldCodeChange,
            PreTxCode = before,
            BlockAccessIndex = Index
        });

        if (changedDuringTx)
        {
            accountChanges.AddCodeChange(new(Index, after.ToArray()));
        }
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0)
        {
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        accountChanges.PopNonceChange(Index, out NonceChange? oldNonceChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.NonceChange,
            PreviousValue = oldNonceChange,
            BlockAccessIndex = Index
        });

        accountChanges.AddNonceChange(new(Index, newNonce));
    }

    public void AddAccountRead(Address address)
    {
        if (_accountReads.Add(address))
        {
            _accountReadsAddedInCurrentIndex.Push(address);
        }
        GetOrAddAccountChanges(address);
    }

    public void AddTransientAccountRead(Address address)
    {
        GetOrAddAccountChanges(address);
    }

    public void AddStorageChange(Address address, UInt256 key, UInt256 before, UInt256 after)
    {
        if (before == after)
        {
            AddStorageRead(address, key);
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        StorageChange(accountChanges, key, before, after);
    }

    public void AddStorageChange(in StorageCell storageCell, UInt256 before, UInt256 after)
        => AddStorageChange(storageCell.Address, storageCell.Index, before, after);

    public void AddStorageRead(in StorageCell storageCell) =>
        AddStorageRead(storageCell.Address, storageCell.Index);

    public void AddStorageRead(Address address, UInt256 key)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (!accountChanges.HasStorageChange(key))
        {
            if (accountChanges.TryAddStorageRead(key))
            {
                _storageReadsAddedInCurrentIndex.Push(new(address, key));
            }
        }
    }

    public void DeleteAccount(Address address, UInt256 oldBalance)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        // Push revertible changes for each storage change that will be cleared.
        // Push ALL changes per slot in reverse order so they restore in correct order (LIFO).
        foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
        {
            // Push changes in reverse order so they restore in original order
            foreach (KeyValuePair<ushort, StorageChange> change in slotChanges.Changes)
            {
                _changes.Push(new()
                {
                    Address = address,
                    Type = ChangeType.StorageChange,
                    Slot = slotChanges.Slot,
                    PreviousValue = change.Value,
                    BlockAccessIndex = change.Value.BlockAccessIndex
                });
            }
        }

        // Push revertible changes for nonce changes (reverse order for correct restore)
        IList<NonceChange> nonceChanges = accountChanges.NonceChanges;
        int nonceCount = nonceChanges.Count;
        for (int i = nonceCount - 1; i >= 0; i--)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousValue = nonceChanges[i],
                BlockAccessIndex = nonceChanges[i].BlockAccessIndex
            });
        }

        // Push revertible changes for code changes (reverse order for correct restore)
        IList<CodeChange> codeChanges = accountChanges.CodeChanges;
        int codeCount = codeChanges.Count;
        for (int i = codeCount - 1; i >= 0; i--)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.CodeChange,
                PreviousValue = codeChanges[i],
                BlockAccessIndex = codeChanges[i].BlockAccessIndex
            });
        }

        foreach (UInt256 storageRead in accountChanges.SelfDestruct())
        {
            _storageReadsAddedInCurrentIndex.Push(new(address, storageRead));
        }
        AddBalanceChange(address, oldBalance, 0);
    }

    private void StorageChange(AccountChanges accountChanges, in UInt256 key, in UInt256 before, in UInt256 after)
    {
        SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(key);

        bool changedDuringTx = HasStorageChangedDuringTx(accountChanges.Address, key, before, after);
        slotChanges.TryPopStorageChange(Index, out StorageChange? oldStorageChange);
        bool removesStorageRead = changedDuringTx && accountChanges.ContainsStorageRead(key);

        _changes.Push(new()
        {
            Address = accountChanges.Address,
            BlockAccessIndex = Index,
            Slot = key,
            Type = ChangeType.StorageChange,
            PreviousValue = oldStorageChange,
            PreTxStorage = before,
            RemovedStorageRead = removesStorageRead
        });

        if (changedDuringTx)
        {
            slotChanges.Changes[Index] = new(Index, after);
            accountChanges.RemoveStorageRead(key);
        }
        else
        {
            if (accountChanges.ClearEmptySlotChangesAndAddRead(key))
            {
                _storageReadsAddedInCurrentIndex.Push(new(accountChanges.Address, key));
            }
        }
    }

    public int TakeSnapshot()
        => _changes.Count;

    public void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            AccountChanges accountChanges = _accountChanges[change.Address];
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    BalanceChange? previousBalance = change.PreviousValue is null ? null : (BalanceChange)change.PreviousValue;

                    // balance could have gone back to pre-tx value
                    // so would already be empty
                    accountChanges.PopBalanceChange(change.BlockAccessIndex, out _); // todo: this index must be the same?
                    if (previousBalance is not null)
                    {
                        accountChanges.AddBalanceChange(previousBalance.Value);
                    }
                    break;
                case ChangeType.CodeChange:
                    CodeChange? previousCode = change.PreviousValue is null ? null : (CodeChange)change.PreviousValue;

                    accountChanges.PopCodeChange(change.BlockAccessIndex, out _);
                    if (previousCode is not null)
                    {
                        accountChanges.AddCodeChange(previousCode.Value);
                    }
                    break;
                case ChangeType.NonceChange:
                    NonceChange? previousNonce = change.PreviousValue is null ? null : (NonceChange)change.PreviousValue;

                    accountChanges.PopNonceChange(change.BlockAccessIndex, out _);
                    if (previousNonce is not null)
                    {
                        accountChanges.AddNonceChange(previousNonce.Value);
                    }
                    break;
                case ChangeType.StorageChange:
                    StorageChange? previousStorage = change.PreviousValue is null ? null : (StorageChange)change.PreviousValue;
                    SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(change.Slot!.Value);

                    slotChanges.TryPopStorageChange(change.BlockAccessIndex, out _);
                    if (previousStorage is not null)
                    {
                        slotChanges.Changes[previousStorage.Value.BlockAccessIndex] = previousStorage.Value;
                        accountChanges.RemoveStorageRead(change.Slot.Value);
                    }

                    if (accountChanges.ClearEmptySlotChangesAndAddRead(change.Slot!.Value) && !change.RemovedStorageRead)
                    {
                        _storageReadsAddedInCurrentIndex.Push(new(change.Address, change.Slot.Value));
                    }
                    break;
            }
        }
    }

    public IEnumerable<ChangeAtIndex> GetChangesAtIndex(ushort index)
    {
        foreach (AccountChanges accountChanges in AccountChanges)
        {
            bool isPostExecutionSystemContract =
                accountChanges.Address == Eip7002Constants.WithdrawalRequestPredeployAddress ||
                accountChanges.Address == Eip7251Constants.ConsolidationRequestPredeployAddress;

            yield return
                new(
                    accountChanges.Address,
                    accountChanges.BalanceChangeAtIndex(index),
                    accountChanges.NonceChangeAtIndex(index),
                    accountChanges.CodeChangeAtIndex(index),
                    accountChanges.SlotChangesAtIndex(index),
                    isPostExecutionSystemContract ? 0 : accountChanges.StorageReads.Count
                );
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"BlockAccessList (Index={Index}, Accounts={_accountChanges.Count})");
        foreach (AccountChanges ac in _accountChanges.Values)
        {
            sb.AppendLine($"  {ac}");
        }
        return sb.ToString();
    }

    // for testing
    internal void AddAccountChanges(params AccountChanges[] accountChanges)
    {
        foreach (AccountChanges change in accountChanges)
        {
            _accountChanges.Add(change.Address, change);
        }
    }

    private bool HasBalanceChangedDuringTx(Address address, UInt256 beforeInstr, UInt256 afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];
        IList<BalanceChange> balanceChanges = accountChanges.BalanceChanges;
        int count = balanceChanges.Count;

        if (count == 0)
        {
            // first balance change of block
            // return balance prior to this instruction
            return beforeInstr != afterInstr;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            BalanceChange balanceChange = balanceChanges[i];
            if (balanceChange.BlockAccessIndex != Index)
            {
                // balance changed in previous tx in block
                return balanceChange.PostBalance != afterInstr;
            }
        }

        // balance only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.BalanceChange && change.Address == address && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxBalance!.Value != afterInstr;
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx balance");
    }

    private bool HasStorageChangedDuringTx(Address address, UInt256 key, in UInt256 beforeInstr, in UInt256 afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];

        if (!accountChanges.TryGetSlotChanges(key, out SlotChanges? slotChanges) || slotChanges.Changes.Count == 0)
        {
            // first storage change of block
            // return storage prior to this instruction
            return beforeInstr != afterInstr;
        }

        IList<StorageChange> values = slotChanges.Changes.Values;
        for (int i = values.Count - 1; i >= 0; i--)
        {
            StorageChange storageChange = values[i];
            if (storageChange.BlockAccessIndex != Index)
            {
                // storage changed in previous tx in block
                return storageChange.NewValue != afterInstr;
            }
        }

        // storage only changed within this transaction
        foreach (Change change in _changes)
        {
            if (
                change.Type == ChangeType.StorageChange &&
                change.Address == address &&
                change.Slot == key &&
                change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxStorage is null || change.PreTxStorage != afterInstr;
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx storage");
    }

    private bool HasCodeChangedDuringTx(Address address, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];
        IList<CodeChange> codeChanges = accountChanges.CodeChanges;
        int count = codeChanges.Count;

        if (count == 0)
        {
            // first code change of block
            // return code prior to this instruction
            return !beforeInstr.SequenceEqual(afterInstr);
        }

        for (int i = count - 1; i >= 0; i--)
        {
            CodeChange codeChange = codeChanges[i];
            if (codeChange.BlockAccessIndex != Index)
            {
                // code changed in previous tx in block
                return !codeChange.NewCode.AsSpan().SequenceEqual(afterInstr);
            }
        }

        // storage only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.CodeChange && change.Address == address && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxCode is null || !change.PreTxCode.AsSpan().SequenceEqual(afterInstr);
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx code");
    }

    private AccountChanges GetOrAddAccountChanges(Address address)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChanges? existing))
        {
            AccountChanges accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
            _accountsAddedInCurrentIndex.Push(address);
            return accountChanges;
        }
        return existing;
    }

    private void RollbackReadsAddedInCurrentIndex()
    {
        while (_storageReadsAddedInCurrentIndex.TryPop(out StorageCell storageCell))
        {
            if (_accountChanges.TryGetValue(storageCell.Address, out AccountChanges? accountChanges))
            {
                accountChanges.RemoveStorageRead(storageCell.Index);
            }
        }

        while (_accountReadsAddedInCurrentIndex.TryPop(out Address? address))
        {
            if (address is not null)
            {
                _accountReads.Remove(address);
            }
        }

        while (_accountsAddedInCurrentIndex.TryPop(out Address? address))
        {
            if (address is not null && _accountChanges.TryGetValue(address, out AccountChanges? accountChanges) && IsEmpty(accountChanges))
            {
                _accountChanges.Remove(address);
            }
        }
    }

    public void PruneEmptyNonReadAccounts()
    {
        List<Address> emptyAccounts = [];
        foreach (KeyValuePair<Address, AccountChanges> item in _accountChanges)
        {
            if (IsEmpty(item.Value))
            {
                if (!_accountReads.Contains(item.Key))
                {
                    emptyAccounts.Add(item.Key);
                }
            }
        }

        foreach (Address address in emptyAccounts)
        {
            _accountChanges.Remove(address);
        }
    }

    public void PruneEmptyAccounts()
    {
        List<Address> emptyAccounts = [];
        foreach (KeyValuePair<Address, AccountChanges> item in _accountChanges)
        {
            if (IsEmpty(item.Value))
            {
                emptyAccounts.Add(item.Key);
            }
        }

        foreach (Address address in emptyAccounts)
        {
            _accountChanges.Remove(address);
            _accountReads.Remove(address);
        }
    }

    private static bool IsEmpty(AccountChanges accountChanges) =>
        accountChanges.StorageReads.Count == 0 &&
        accountChanges.StorageChanges.Count == 0 &&
        accountChanges.BalanceChanges.Count == 0 &&
        accountChanges.NonceChanges.Count == 0 &&
        accountChanges.CodeChanges.Count == 0;

    private enum ChangeType
    {
        BalanceChange = 0,
        CodeChange = 1,
        NonceChange = 2,
        StorageChange = 3
    }

    private readonly struct Change
    {
        public Address Address { get; init; }
        public UInt256? Slot { get; init; }
        public ChangeType Type { get; init; }
        public IIndexedChange? PreviousValue { get; init; }
        public UInt256? PreTxBalance { get; init; }
        public UInt256? PreTxStorage { get; init; }
        public byte[]? PreTxCode { get; init; }
        public ushort BlockAccessIndex { get; init; }
        public bool RemovedStorageRead { get; init; }
    }
}

public record struct ChangeAtIndex(Address Address, BalanceChange? BalanceChange, NonceChange? NonceChange, CodeChange? CodeChange, IEnumerable<SlotChanges> SlotChanges, int Reads);
