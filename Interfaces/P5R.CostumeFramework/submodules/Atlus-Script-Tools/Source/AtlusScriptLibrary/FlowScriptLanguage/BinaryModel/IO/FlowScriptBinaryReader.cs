﻿using AtlusScriptLibrary.Common.IO;
using AtlusScriptLibrary.Common.Text.Encodings;
using AtlusScriptLibrary.MessageScriptLanguage.BinaryModel;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AtlusScriptLibrary.FlowScriptLanguage.BinaryModel.IO;

public sealed class FlowScriptBinaryReader : IDisposable
{
    private bool mDisposed;
    private long mPositionBase;
    private EndianBinaryReader mReader;
    private BinaryFormatVersion mVersion;

    public FlowScriptBinaryReader(Stream stream, BinaryFormatVersion version, bool leaveOpen = false)
    {
        mPositionBase = stream.Position;
        mReader = new EndianBinaryReader(stream, ShiftJISEncoding.Instance, leaveOpen, version.HasFlag(BinaryFormatVersion.BigEndian) ? Endianness.BigEndian : Endianness.LittleEndian);
        mVersion = version;
    }

    public FlowScriptBinary ReadBinary(BinaryFormatVersion version)
    {
        FlowScriptBinary instance = new FlowScriptBinary
        {
            mHeader = ReadHeader()
        };

        instance.mSectionHeaders = ReadSectionHeaders(ref instance.mHeader);

        for (int i = 0; i < instance.mSectionHeaders.Length; i++)
        {
            ref var sectionHeader = ref instance.mSectionHeaders[i];

            switch (sectionHeader.SectionType)
            {
                case BinarySectionType.ProcedureLabelSection:
                case BinarySectionType.JumpLabelSection:
                    DetectBinaryFormatVersion(ref instance.mHeader, ref sectionHeader);
                    var labelSection = ReadLabelSection(ref sectionHeader);
                    if (sectionHeader.SectionType == BinarySectionType.ProcedureLabelSection)
                        instance.mProcedureLabelSection = labelSection;
                    else
                        instance.mJumpLabelSection = labelSection;
                    break;

                case BinarySectionType.TextSection:
                    instance.mTextSection = ReadTextSection(ref sectionHeader, version);
                    break;

                case BinarySectionType.MessageScriptSection:
                    instance.mMessageScriptSection = ReadMessageScriptSection(ref sectionHeader);
                    break;

                case BinarySectionType.StringSection:

                    // fix for early, broken files
                    // see: nocturne e500.bf
                    if (sectionHeader.FirstElementAddress == instance.mHeader.FileSize)
                    {
                        instance.mHeader.FileSize = (int)(mReader.BaseStreamLength - mPositionBase);
                        sectionHeader.ElementCount = instance.mHeader.FileSize - sectionHeader.FirstElementAddress;
                    }

                    instance.mStringSection = ReadStringSection(ref sectionHeader);
                    break;

                default:
                    throw new InvalidDataException("Unknown section type");
            }
        }

        instance.mFormatVersion = GetDetectedFormatVersion();

        return instance;
    }

    public BinaryHeader ReadHeader()
    {
        ReadHeaderInternal(out BinaryHeader header);
        MaybeSwapHeaderEndianness(ref header);

        return header;
    }

    public BinarySectionHeader[] ReadSectionHeaders(ref BinaryHeader header)
    {
        return mReader.ReadStruct<BinarySectionHeader>(header.SectionCount);
    }

    public BinaryLabel[] ReadLabelSection(ref BinarySectionHeader sectionHeader)
    {
        EnsureSectionHeaderInitialValidState(ref sectionHeader);

        if (sectionHeader.ElementSize != BinaryLabel.SIZE_V1 &&
            sectionHeader.ElementSize != BinaryLabel.SIZE_V2 &&
            sectionHeader.ElementSize != BinaryLabel.SIZE_V3 &&
            sectionHeader.ElementSize != BinaryLabel.SIZE_V4)
        {
            throw new InvalidDataException("Unknown size for label");
        }

        var labels = new BinaryLabel[sectionHeader.ElementCount];

        for (int i = 0; i < labels.Length; i++)
        {
            // length of string is equal to the size of the label without the 2 Int32 fields
            int nameStringLength = sectionHeader.ElementSize - (sizeof(int) * 2);

            var label = new BinaryLabel
            {
                Name = mReader.ReadString(StringBinaryFormat.FixedLength, nameStringLength),
                InstructionIndex = mReader.ReadInt32(),
                Reserved = mReader.ReadInt32()
            };

            // Would indicate a possible endianness issue
            if (label.InstructionIndex >= int.MaxValue)
            {
                throw new InvalidDataException("Invalid label offset");
            }

            // Should be zero
            if (label.Reserved != 0)
            {
                throw new InvalidDataException("Label reserved field isn't 0");
            }

            labels[i] = label;
        }

        return labels;
    }

    public BinaryInstruction[] ReadTextSection(ref BinarySectionHeader sectionHeader, BinaryFormatVersion version)
    {
        EnsureSectionHeaderInitialValidState(ref sectionHeader);

        if (sectionHeader.ElementSize != BinaryInstruction.SIZE)
        {
            throw new InvalidDataException($"{BinarySectionType.TextSection} unit size must be 4");
        }
        var instructions = new BinaryInstruction[sectionHeader.ElementCount];
        if (version == BinaryFormatVersion.Version4) mReader.Endianness = Endianness.BigEndian; // force big endian for instructions
        for (int i = 0; i < instructions.Length; i++)
        {
            BinaryInstruction instruction = new BinaryInstruction();

            if (i != 0 && instructions[i - 1].Opcode == Opcode.PUSHI)
            {
                instruction.Opcode = unchecked((Opcode)(-1));
                instruction.OperandUInt = mReader.ReadUInt32();
            }
            else if (i != 0 && instructions[i - 1].Opcode == Opcode.PUSHF)
            {
                instruction.Opcode = unchecked((Opcode)(-1));
                instruction.OperandFloat = mReader.ReadSingle();
            }
            else
            {
                instruction.Opcode = (Opcode)mReader.ReadInt16();
                instruction.OperandUShort = mReader.ReadUInt16();
            }

            instructions[i] = instruction;
        }
        if (version == BinaryFormatVersion.Version4) mReader.Endianness = Endianness.LittleEndian;

        return instructions;
    }

    public IMessageScriptBinary ReadMessageScriptSection(ref BinarySectionHeader sectionHeader)
    {
        EnsureSectionHeaderInitialValidState(ref sectionHeader);

        if (sectionHeader.ElementSize != sizeof(byte))
        {
            throw new InvalidDataException($"{BinarySectionType.MessageScriptSection} unit size must be 1");
        }

        if (sectionHeader.ElementCount != 0)
        {
            var bytes = mReader.ReadBytes(sectionHeader.ElementCount);
            using (var memoryStream = new MemoryStream(bytes))
            {
                return MessageScriptBinaryFactory.FromStream(memoryStream);
            }
        }
        return null;
    }

    public byte[] ReadStringSection(ref BinarySectionHeader sectionHeader)
    {
        EnsureSectionHeaderInitialValidState(ref sectionHeader);

        if (sectionHeader.ElementSize != sizeof(byte))
        {
            throw new InvalidDataException($"{BinarySectionType.StringSection} unit size must be 1");
        }

        return mReader.ReadBytes(sectionHeader.ElementCount);
    }

    public BinaryFormatVersion GetDetectedFormatVersion()
    {
        return mVersion;
    }

    public void Dispose()
    {
        if (mDisposed)
            return;

        ((IDisposable)mReader).Dispose();
        mDisposed = true;
    }

    private void ReadHeaderInternal(out BinaryHeader header)
    {
        // Check if the stream isn't too small to be a proper file
        if (mReader.BaseStreamLength < BinaryHeader.SIZE)
        {
            throw new InvalidDataException("Stream is too small to be valid");
        }
        header = mReader.ReadStruct<BinaryHeader>();
        if (!header.Magic.SequenceEqual(BinaryHeader.MAGIC) && !header.Magic.SequenceEqual(BinaryHeader.MAGIC_REVERSED))
        {
            throw new InvalidDataException("Header magic value does not match");
        }
    }

    private void MaybeSwapHeaderEndianness(ref BinaryHeader header)
    {
        // Swap endianness if high bits of section count are used
        if ((header.SectionCount & 0xFF000000) != 0)
        {
            header = EndiannessHelper.Swap(header);

            if (mReader.Endianness == Endianness.LittleEndian)
            {
                mReader.Endianness = Endianness.BigEndian;
                mVersion |= BinaryFormatVersion.BigEndian;
            }
            else
            {
                mReader.Endianness = Endianness.LittleEndian;
                mVersion ^= BinaryFormatVersion.BigEndian;
            }
        }
    }

    private void EnsureSectionHeaderInitialValidState(ref BinarySectionHeader sectionHeader)
    {
        if (sectionHeader.FirstElementAddress == 0)
        {
            throw new InvalidOperationException("Section start offset is a null pointer");
        }

        long absoluteAddress = mPositionBase + sectionHeader.FirstElementAddress;

        if (!(absoluteAddress + (sectionHeader.ElementSize * sectionHeader.ElementCount) <= mReader.BaseStreamLength))
        {
            throw new InvalidDataException("Stream is too small for the amount of data described. File is likely truncated");
        }

        mReader.SeekBegin(absoluteAddress);
    }

    private void DetectBinaryFormatVersion(ref BinaryHeader header, ref BinarySectionHeader sectionHeader)
    {
        if (sectionHeader.ElementSize == BinaryLabel.SIZE_V1)
        {
            if (!mVersion.HasFlag(BinaryFormatVersion.Version1))
            {
                mVersion = BinaryFormatVersion.Version1;
                if (mReader.Endianness == Endianness.BigEndian)
                    mVersion |= BinaryFormatVersion.BigEndian;
            }
        }
        else if (sectionHeader.ElementSize == BinaryLabel.SIZE_V2 &&
                 sectionHeader.ElementSize == BinaryLabel.SIZE_V4)
        {
            if (header.Magic.SequenceEqual(BinaryHeader.MAGIC_REVERSED))
            {
                if (!mVersion.HasFlag(BinaryFormatVersion.Version4))
                {
                    mVersion = BinaryFormatVersion.Version4;
                    if (mReader.Endianness == Endianness.BigEndian)
                        mVersion |= BinaryFormatVersion.BigEndian;
                }
            }
            else
            {
                if (!mVersion.HasFlag(BinaryFormatVersion.Version2))
                {
                    mVersion = BinaryFormatVersion.Version2;
                    if (mReader.Endianness == Endianness.BigEndian)
                        mVersion |= BinaryFormatVersion.BigEndian;
                }
            }
        }
        else if (sectionHeader.ElementSize == BinaryLabel.SIZE_V3)
        {
            if (!mVersion.HasFlag(BinaryFormatVersion.Version3))
            {
                mVersion = BinaryFormatVersion.Version3;
                if (mReader.Endianness == Endianness.BigEndian)
                    mVersion |= BinaryFormatVersion.BigEndian;
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported format version: label section size {sectionHeader.ElementSize}");
        }
    }
}
