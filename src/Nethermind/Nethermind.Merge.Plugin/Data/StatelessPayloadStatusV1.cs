// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data;

public class StatelessPayloadStatusV1
{
    public static StatelessPayloadStatusV1 Invalid(string? validationError = null) => new()
    {
        Status = PayloadStatus.Invalid,
        ValidationError = validationError
    };

    public static StatelessPayloadStatusV1 Valid(Hash256 stateRoot, Hash256 receiptsRoot) => new()
    {
        Status = PayloadStatus.Valid,
        StateRoot = stateRoot,
        ReceiptsRoot = receiptsRoot
    };

    public string Status { get; set; } = PayloadStatus.Invalid;

    public Hash256 StateRoot { get; set; } = Keccak.Zero;

    public Hash256 ReceiptsRoot { get; set; } = Keccak.Zero;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidationError { get; set; }
}
