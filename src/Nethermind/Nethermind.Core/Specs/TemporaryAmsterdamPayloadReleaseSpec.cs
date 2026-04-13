// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core.Specs;

/// <summary>
/// Temporary compatibility spec for Amsterdam-era payloads that are still delivered
/// without block-level access list and slot fields on the EPBS devnet branch.
/// </summary>
public sealed class TemporaryAmsterdamPayloadReleaseSpec(IReleaseSpec spec) : ReleaseSpecDecorator(spec), IReleaseSpec
{
    private Array? _evmInstructionsNoTrace;
    private Array? _evmInstructionsTraced;
    private SpecGasCosts? _gasCosts;

    public static bool IsActive(IReleaseSpec spec, BlockHeader header) =>
        spec.IsEip7928Enabled &&
        header.BlockAccessListHash is null &&
        header.SlotNumber is null;

    public override long MaxCodeSize => IsEip7907Enabled
        ? CodeSizeConstants.MaxCodeSizeEip7907
        : IsEip170Enabled
            ? CodeSizeConstants.MaxCodeSizeEip170
            : long.MaxValue;

    public override bool IsEip7708Enabled => false;
    public override bool IsEip7778Enabled => false;
    public override bool IsEip7843Enabled => false;
    public override bool IsEip7928Enabled => false;
    public override bool IsEip7954Enabled => false;
    public override bool IsEip8024Enabled => false;
    public override bool IsEip8037Enabled => false;

    Array? IReleaseSpec.EvmInstructionsNoTrace
    {
        get => _evmInstructionsNoTrace;
        set => _evmInstructionsNoTrace = value;
    }

    Array? IReleaseSpec.EvmInstructionsTraced
    {
        get => _evmInstructionsTraced;
        set => _evmInstructionsTraced = value;
    }

    SpecGasCosts IReleaseSpec.GasCosts => _gasCosts ??= new SpecGasCosts(this);
}
