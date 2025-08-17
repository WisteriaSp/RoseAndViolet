﻿using System;

namespace AtlusScriptLibrary.FlowScriptLanguage.BinaryModel;

public enum BinarySectionType : uint
{
    ProcedureLabelSection = 0,
    JumpLabelSection = 1,
    TextSection = 2,
    MessageScriptSection = 3,
    StringSection = 4,
}

[Flags]
public enum BinaryFormatVersion : uint
{
    Unknown = 1 << 0,
    Version1 = 1 << 1,
    Version2 = 1 << 2,
    Version3 = 1 << 3,
    Version4 = 1 << 4,
    BigEndian = 1 << 15,
    Version1BigEndian = Version1 | BigEndian,
    Version2BigEndian = Version2 | BigEndian,
    Version3BigEndian = Version3 | BigEndian,
    Version4BigEndian = Version4 | BigEndian,
}
