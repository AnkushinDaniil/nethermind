// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

/// <summary>
/// Non-generic intrinsic gas result for backward compatibility.
/// </summary>
public readonly record struct EthereumIntrinsicGas(long Standard, long FloorGas)
{
    public long MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator long(EthereumIntrinsicGas gas) => gas.MinimalGas;
    public static implicit operator EthereumIntrinsicGas(IntrinsicGas<EthereumGasPolicy> gas) =>
        new(gas.Standard.Value + gas.Standard.StateReservoir, gas.FloorGas.Value);
}

public static class IntrinsicGasCalculator
{
    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type, allowing MultiGas breakdown for Arbitrum.
    /// </summary>
    private static IntrinsicGas<TGasPolicy> Calculate<TGasPolicy>(Transaction transaction, IReleaseSpec releaseSpec, Eip8037GasCosts eip8037GasCosts)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.CalculateIntrinsicGas(transaction, releaseSpec, eip8037GasCosts);

    /// <summary>
    /// Non-generic backward-compatible Calculate method.
    /// </summary>
    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec) =>
        Calculate(transaction, releaseSpec, Eip8037GasCosts.Default);

    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec, long blockGasLimit) =>
        Calculate(transaction, releaseSpec, Eip8037GasCosts.FromBlockGasLimit(blockGasLimit));

    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec, Eip8037GasCosts eip8037GasCosts) =>
        Calculate<EthereumGasPolicy>(transaction, releaseSpec, eip8037GasCosts);

    public static long AccessListCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        IGasPolicy<EthereumGasPolicy>.AccessListCost(transaction, releaseSpec);
}
