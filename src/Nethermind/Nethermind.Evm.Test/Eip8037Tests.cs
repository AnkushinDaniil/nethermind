// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037Tests
{
    private static IEnumerable<TestCaseData> ConstantsTestCases()
    {
        Eip8037GasCosts costs = Eip8037GasCosts.Default;
        yield return new TestCaseData(costs.CostPerStateByte).Returns(1174L).SetName("CostPerStateByte");
        yield return new TestCaseData(costs.SSetState).Returns(37568L).SetName("SSetState");
        yield return new TestCaseData(costs.CreateState).Returns(131488L).SetName("CreateState");
        yield return new TestCaseData(costs.NewAccountState).Returns(131488L).SetName("NewAccountState");
        yield return new TestCaseData(costs.PerAuthBaseState).Returns(27002L).SetName("PerAuthBaseState");
    }

    [TestCaseSource(nameof(ConstantsTestCases))]
    public long Constants_are_calculated_correctly(long actual) => actual;

    [TestCase(1, ExpectedResult = 1L)]
    [TestCase(50_000_000, ExpectedResult = 150L)]
    [TestCase(100_000_000, ExpectedResult = 1174L)]
    [TestCase(150_000_000, ExpectedResult = 1686L)]
    [TestCase(200_000_000, ExpectedResult = 2198L)]
    [TestCase(1_000_000_000, ExpectedResult = 11926L)]
    public long Cost_per_state_byte_is_derived_from_block_gas_limit(long blockGasLimit) =>
        Eip8037GasCosts.CalculateCostPerStateByte(blockGasLimit);

    [TestCase(1, ExpectedResult = 6L)]
    [TestCase(32, ExpectedResult = 6L)]
    [TestCase(33, ExpectedResult = 12L)]
    public long Code_deposit_regular_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out long regularCost, out _);
        return regularCost;
    }

    [TestCase(1, ExpectedResult = 1174L)]
    [TestCase(32, ExpectedResult = 37568L)]
    [TestCase(33, ExpectedResult = 38742L)]
    public long Code_deposit_state_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out _, out long stateCost);
        return stateCost;
    }

    [Test]
    public void Code_deposit_state_cost_uses_dynamic_cost_per_byte()
    {
        Eip8037GasCosts costs = Eip8037GasCosts.FromBlockGasLimit(150_000_000);

        CodeDepositHandler.CalculateCost(Amsterdam.Instance, costs, 33, out _, out long stateCost);

        Assert.That(stateCost, Is.EqualTo(33 * 1686L));
    }

    [Test]
    public void State_gas_consumption_spills_to_regular_gas()
    {
        EthereumGasPolicy gas = new() { Value = 100, StateReservoir = 50, StateGasUsed = 0 };

        bool consumed = EthereumGasPolicy.ConsumeStateGas(ref gas, 70);

        Assert.That((consumed, gas.Value, gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((true, 80L, 0L, 70L)));
    }

    [Test]
    public void Child_frame_gets_full_state_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 333, StateGasUsed = 50 };

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, child.Value, child.StateReservoir, child.StateGasUsed),
            Is.EqualTo((1_000L, 0L, 50L, 444L, 333L, 0L)));
    }

    [Test]
    public void Child_frame_refund_restores_remaining_state_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 333, StateGasUsed = 50 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);
        EthereumGasPolicy.ConsumeStateGas(ref child, 100);
        EthereumGasPolicy.UpdateGas(ref child, 150);

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((1_294L, 233L, 150L)));
    }

    [Test]
    public void State_refund_is_clamped_to_intrinsic_state_floor()
    {
        EthereumGasPolicy gas = new() { Value = 100, StateReservoir = 0, StateGasUsed = 120 };

        EthereumGasPolicy.RefundStateGas(ref gas, 200, stateGasFloor: 40);

        Assert.That((gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((200L, 0L)));
    }

    [Test]
    public void Code_insert_state_refund_is_available_to_later_state_gas()
    {
        Eip8037GasCosts costs = Eip8037GasCosts.Default;
        long intrinsicAuthState = costs.NewAccountState + costs.PerAuthBaseState;
        EthereumGasPolicy gas = new()
        {
            StateGasUsed = intrinsicAuthState,
            CostPerStateByte = costs.CostPerStateByte,
        };

        long regularRefund = EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas, 1, Amsterdam.Instance, intrinsicAuthState);
        EthereumGasPolicy.ConsumeStateGas(ref gas, costs.SSetState);
        EthereumGasPolicy.ConsumeStateGas(ref gas, costs.SSetState);

        Assert.That(regularRefund, Is.Zero);
        Assert.That((gas.StateReservoir, gas.StateGasUsed),
            Is.EqualTo((costs.NewAccountState - 2 * costs.SSetState, costs.PerAuthBaseState + 2 * costs.SSetState)));
    }

    [Test]
    public void Dynamic_costs_are_carried_by_gas_policy()
    {
        Eip8037GasCosts costs = Eip8037GasCosts.FromBlockGasLimit(150_000_000);
        EthereumGasPolicy gas = EthereumGasPolicy.WithEip8037GasCosts(EthereumGasPolicy.FromLong(500_000), costs);

        bool consumed = EthereumGasPolicy.ConsumeNewAccountCreation<OnFlag>(ref gas);

        Assert.That(consumed, Is.True);
        Assert.That(gas.StateGasUsed, Is.EqualTo(costs.NewAccountState));
        Assert.That(EthereumGasPolicy.GetEip8037GasCosts(in gas), Is.EqualTo(costs));

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref gas, 10_000);
        Assert.That(EthereumGasPolicy.GetEip8037GasCosts(in child), Is.EqualTo(costs));
    }

    [Test]
    public void Exceptional_halt_preserves_state_gas()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 500, StateGasUsed = 10 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);

        EthereumGasPolicy.SetOutOfGas(ref child);
        Assert.That((child.Value, child.StateReservoir), Is.EqualTo((0L, 300L)));

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 500);
        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((500L, 10L)));
    }

    [Test]
    public void Revert_restores_state_gas_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 400, StateGasUsed = 20 };
        EthereumGasPolicy.Consume(ref parent, 600);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.UpdateGas(ref child, 100);
        EthereumGasPolicy.ConsumeStateGas(ref child, 150);

        EthereumGasPolicy.UpdateGasUp(ref parent, EthereumGasPolicy.GetRemainingGas(in child));
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 400);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((900L, 400L, 20L)));
    }

    [TestCase(ExpectedResult = 5_000L)]
    public long Spent_gas_subtracts_state_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 3_000, StateReservoir = 2_000, StateGasUsed = 500 };
        return 10_000L - EthereumGasPolicy.GetRemainingGas(in gas) - EthereumGasPolicy.GetStateReservoir(in gas);
    }
}
