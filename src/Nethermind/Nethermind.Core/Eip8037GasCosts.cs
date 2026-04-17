// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Core;

/// <summary>
/// EIP-8037 state-creation gas costs derived from the block gas limit.
/// </summary>
public readonly struct Eip8037GasCosts : IEquatable<Eip8037GasCosts>
{
    public const long DefaultBlockGasLimit = 100_000_000L;

    private const ulong TargetStateGrowthPerYear = 100UL * 1024 * 1024 * 1024;
    private const ulong CostNumeratorMultiplier = 2_628_000UL;
    private const ulong CostDenominator = 2 * TargetStateGrowthPerYear;
    private const int CpsbSignificantBits = 5;
    private const ulong CpsbOffset = 9_578UL;

    public static Eip8037GasCosts Default { get; } = FromBlockGasLimit(DefaultBlockGasLimit);

    public Eip8037GasCosts(long costPerStateByte)
    {
        CostPerStateByte = costPerStateByte;
        SSetState = 32 * costPerStateByte;
        CreateState = 112 * costPerStateByte;
        NewAccountState = 112 * costPerStateByte;
        CodeDepositState = costPerStateByte;
        PerAuthBaseState = 23 * costPerStateByte;
        PerEmptyAccountState = 112 * costPerStateByte;
        SSetReversedRefund = SSetState + GasCostOf.SSetRegular - GasCostOf.WarmStateRead;
    }

    public long CostPerStateByte { get; }
    public long SSetState { get; }
    public long CreateState { get; }
    public long NewAccountState { get; }
    public long CodeDepositState { get; }
    public long PerAuthBaseState { get; }
    public long PerEmptyAccountState { get; }
    public long SSetReversedRefund { get; }

    public static Eip8037GasCosts FromBlockGasLimit(long blockGasLimit) =>
        new(CalculateCostPerStateByte(blockGasLimit));

    public static long CalculateCostPerStateByte(long blockGasLimit)
    {
        if (blockGasLimit <= 0)
            return 1;

        UInt128 numerator = (UInt128)(ulong)blockGasLimit * CostNumeratorMultiplier;
        ulong raw = (ulong)((numerator + CostDenominator - 1) / CostDenominator);
        ulong shifted = raw + CpsbOffset;
        int shift = Math.Max(64 - BitOperations.LeadingZeroCount(shifted) - CpsbSignificantBits, 0);
        ulong rounded = (shifted >> shift) << shift;
        long quantized = rounded > CpsbOffset ? (long)(rounded - CpsbOffset) : 0;
        return Math.Max(quantized, 1L);
    }

    public bool Equals(Eip8037GasCosts other) => CostPerStateByte == other.CostPerStateByte;
    public override bool Equals(object? obj) => obj is Eip8037GasCosts other && Equals(other);
    public override int GetHashCode() => CostPerStateByte.GetHashCode();
}
