// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base
{
    public class TestBlockJson
    {
        public TestBlockHeaderJson? BlockHeader { get; set; }
        public TestBlockHeaderJson[]? UncleHeaders { get; set; }
        public string? Rlp { get; set; }
        public TestBlockAccessListAccountJson[]? BlockAccessList { get; set; }
        public LegacyTransactionJson[]? Transactions { get; set; }
        public string? ExpectException { get; set; }
    }

    public class TestBlockAccessListAccountJson
    {
        public string Address { get; set; } = null!;
        public TestBlockAccessListStorageSlotJson[]? StorageChanges { get; set; }
        public string[]? StorageReads { get; set; }
        public TestBlockAccessListBalanceChangeJson[]? BalanceChanges { get; set; }
        public TestBlockAccessListNonceChangeJson[]? NonceChanges { get; set; }
        public TestBlockAccessListCodeChangeJson[]? CodeChanges { get; set; }
    }

    public class TestBlockAccessListStorageSlotJson
    {
        public string Slot { get; set; } = null!;
        public TestBlockAccessListStorageChangeJson[]? SlotChanges { get; set; }
    }

    public class TestBlockAccessListStorageChangeJson
    {
        public string BlockAccessIndex { get; set; } = null!;
        public string PostValue { get; set; } = null!;
    }

    public class TestBlockAccessListBalanceChangeJson
    {
        public string BlockAccessIndex { get; set; } = null!;
        public string PostBalance { get; set; } = null!;
    }

    public class TestBlockAccessListNonceChangeJson
    {
        public string BlockAccessIndex { get; set; } = null!;
        public string PostNonce { get; set; } = null!;
    }

    public class TestBlockAccessListCodeChangeJson
    {
        public string BlockAccessIndex { get; set; } = null!;
        public string NewCode { get; set; } = null!;
    }
}
