﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Formats.Nrbf.Utils;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Reflection.Metadata;

namespace System.Formats.Nrbf;

/// <summary>
/// Provides stateless methods for decoding .NET Remoting Binary Format (NRBF) encoded data.
/// </summary>
/// <remarks>
/// NrbfDecoder is an implementation of an NRBF reader, but its behaviors don't strictly follow BinaryFormatter's implementation.
/// You shouldn't use the output of NrbfDecoder to determine whether a call to BinaryFormatter would be safe.
/// </remarks>
public static class NrbfDecoder
{
    private static UTF8Encoding ThrowOnInvalidUtf8Encoding { get; } = new(false, throwOnInvalidBytes: true);

    // The header consists of:
    // - a byte that describes the record type (SerializationRecordType.SerializedStreamHeader)
    // - four 32 bit integers:
    //   - root Id (every value except of 0 is valid)
    //   - header Id (value is ignored)
    //   - major version, it has to be equal 1.
    //   - minor version, it has to be equal 0.
    private static ReadOnlySpan<byte> HeaderSuffix => [1, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// Checks if the given buffer starts with the <see href="https://learn.microsoft.com/openspecs/windows_protocols/ms-nrbf/a7e578d3-400a-4249-9424-7529d10d1b3c">NRBF payload header</see>.
    /// </summary>
    /// <param name="bytes">The buffer to inspect.</param>
    /// <returns><see langword="true" /> if the buffer starts with the NRBF payload header; otherwise, <see langword="false" />.</returns>
    public static bool StartsWithPayloadHeader(ReadOnlySpan<byte> bytes)
        => bytes.Length >= SerializedStreamHeaderRecord.Size
        && bytes[0] == (byte)SerializationRecordType.SerializedStreamHeader
        && bytes.Slice(SerializedStreamHeaderRecord.Size - HeaderSuffix.Length, HeaderSuffix.Length).SequenceEqual(HeaderSuffix);

    /// <summary>
    /// Checks if the given stream starts with the <see href="https://learn.microsoft.com/openspecs/windows_protocols/ms-nrbf/a7e578d3-400a-4249-9424-7529d10d1b3c">NRBF payload header</see>.
    /// </summary>
    /// <param name="stream">The stream to inspect. The stream must be both readable and seekable.</param>
    /// <returns><see langword="true" /> if the stream starts with the NRBF payload header; otherwise, <see langword="false" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is <see langword="null" />.</exception>
    /// <exception cref="NotSupportedException">The stream does not support reading or seeking.</exception>
    /// <exception cref="ObjectDisposedException">The stream was closed.</exception>
    /// <exception cref="IOException">An I/O error occurred.</exception>
    /// <remarks>When this method returns, <paramref name="stream" /> is restored to its original position.</remarks>
    public static bool StartsWithPayloadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException(SR.Argument_NonSeekableStream, nameof(stream));
        }

        long beginning = stream.Position;
        if (stream.Length - beginning <= SerializedStreamHeaderRecord.Size)
        {
            return false;
        }

        byte[] buffer = new byte[SerializedStreamHeaderRecord.Size];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                stream.Position = beginning;
                return false;
            }
            offset += read;
        }

        bool result = StartsWithPayloadHeader(buffer);
        stream.Position = beginning;
        return result;
    }

    /// <summary>
    /// Decodes the provided NRBF payload.
    /// </summary>
    /// <param name="payload">The NRBF payload.</param>
    /// <param name="options">Options to control behavior during parsing.</param>
    /// <param name="leaveOpen">
    ///   <see langword="true" /> to leave <paramref name="payload"/> payload open
    ///   after the reading is finished; otherwise, <see langword="false" />.
    /// </param>
    /// <returns>A <see cref="SerializationRecord"/> that represents the root object.
    /// It can be either <see cref="PrimitiveTypeRecord{T}"/>,
    /// a <see cref="ClassRecord"/>, or an <see cref="ArrayRecord"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="payload"/> does not support reading or is already closed.</exception>
    /// <exception cref="SerializationException">Reading from <paramref name="payload"/> encountered invalid NRBF data.</exception>
    /// <exception cref="IOException">An I/O error occurred.</exception>
    /// <exception cref="NotSupportedException">
    /// Reading from <paramref name="payload"/> encountered unsupported records,
    /// for example, arrays with non-zero offset or unsupported record types
    /// (<see cref="SerializationRecordType.ClassWithMembers"/>, <see cref="SerializationRecordType.SystemClassWithMembers"/>,
    /// <see cref="SerializationRecordType.MethodCall"/>, or <see cref="SerializationRecordType.MethodReturn"/>).
    /// </exception>
    /// <exception cref="DecoderFallbackException">Reading from <paramref name="payload"/>
    /// encountered an invalid UTF8 sequence.</exception>
    /// <exception cref="EndOfStreamException">The end of the stream was reached before reading <see cref="SerializationRecordType.MessageEnd"/> record.</exception>
    public static SerializationRecord Decode(Stream payload, PayloadOptions? options = default, bool leaveOpen = false)
        => Decode(payload, out _, options, leaveOpen);

    /// <param name="payload">The NRBF payload.</param>
    /// <param name="recordMap">
    ///   When this method returns, contains a mapping of <see cref="SerializationRecordId" /> to the associated serialization record.
    ///   This parameter is treated as uninitialized.
    /// </param>
    /// <param name="options">An object that describes optional <see cref="PayloadOptions"/> parameters to use.</param>
    /// <param name="leaveOpen">
    ///   <see langword="true" /> to leave <paramref name="payload"/> payload open
    ///   after the reading is finished; otherwise, <see langword="false" />.
    /// </param>
    /// <inheritdoc cref="Decode(Stream, PayloadOptions?, bool)"/>
    public static SerializationRecord Decode(Stream payload, out IReadOnlyDictionary<SerializationRecordId, SerializationRecord> recordMap, PayloadOptions? options = default, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using BinaryReader reader = new(payload, ThrowOnInvalidUtf8Encoding, leaveOpen: leaveOpen);
        try
        {
            return Decode(reader, options ?? new(), out recordMap);
        }
        catch (FormatException) // can be thrown by various BinaryReader methods
        {
            throw new SerializationException(SR.Serialization_InvalidFormat);
        }
    }

    /// <summary>
    /// Decodes the provided NRBF payload that is expected to contain an instance of any class (or struct) that is not an <see cref="Array"/> or a primitive type.
    /// </summary>
    /// <returns>A <see cref="ClassRecord"/> that represents the root object.</returns>
    /// <inheritdoc cref="Decode(Stream, PayloadOptions?, bool)"/>
    public static ClassRecord DecodeClassRecord(Stream payload, PayloadOptions? options = default, bool leaveOpen = false)
        => (ClassRecord)Decode(payload, options, leaveOpen);

    private static SerializationRecord Decode(BinaryReader reader, PayloadOptions options, out IReadOnlyDictionary<SerializationRecordId, SerializationRecord> readOnlyRecordMap)
    {
        Stack<NextInfo> readStack = new();
        RecordMap recordMap = new();

        // Everything has to start with a header
        var header = (SerializedStreamHeaderRecord)DecodeNext(reader, recordMap, AllowedRecordTypes.SerializedStreamHeader, options, out _);
        // and can be followed by any Object, BinaryLibrary and a MessageEnd.
        const AllowedRecordTypes Allowed = AllowedRecordTypes.AnyObject
            | AllowedRecordTypes.BinaryLibrary | AllowedRecordTypes.MessageEnd;

        SerializationRecordType recordType;
        SerializationRecord nextRecord;
        do
        {
            while (readStack.Count > 0)
            {
                NextInfo nextInfo = readStack.Pop();

                if (nextInfo.Allowed != AllowedRecordTypes.None)
                {
                    // Decode the next Record
                    do
                    {
                        nextRecord = DecodeNext(reader, recordMap, nextInfo.Allowed, options, out _);
                        // BinaryLibrary often precedes class records.
                        // It has been already added to the RecordMap and it must not be added
                        // to the array record, so simply read next record.
                        // It's possible to read multiple BinaryLibraryRecord in a row, hence the loop.
                    }
                    while (nextRecord is BinaryLibraryRecord);

                    // Handle it:
                    // - add to the parent records list,
                    // - push next info if there are remaining nested records to read.
                    nextInfo.Parent.HandleNextRecord(nextRecord, nextInfo);
                    // Push on the top of the stack the first nested record of last read record,
                    // so it gets read as next record.
                    PushFirstNestedRecordInfo(nextRecord, readStack);
                }
                else
                {
                    object value = reader.ReadPrimitiveValue(nextInfo.PrimitiveType);

                    nextInfo.Parent.HandleNextValue(value, nextInfo);
                }
            }

            nextRecord = DecodeNext(reader, recordMap, Allowed, options, out recordType);
            PushFirstNestedRecordInfo(nextRecord, readStack);
        }
        while (recordType != SerializationRecordType.MessageEnd);

        readOnlyRecordMap = recordMap;
        return recordMap.GetRootRecord(header);
    }

    private static SerializationRecord DecodeNext(BinaryReader reader, RecordMap recordMap,
        AllowedRecordTypes allowed, PayloadOptions options, out SerializationRecordType recordType)
    {
        recordType = reader.ReadSerializationRecordType(allowed);

        SerializationRecord record = recordType switch
        {
            SerializationRecordType.ArraySingleObject => ArraySingleObjectRecord.Decode(reader),
            SerializationRecordType.ArraySinglePrimitive => DecodeArraySinglePrimitiveRecord(reader),
            SerializationRecordType.ArraySingleString => ArraySingleStringRecord.Decode(reader),
            SerializationRecordType.BinaryArray => DecodeBinaryArrayRecord(reader, recordMap, options),
            SerializationRecordType.BinaryLibrary => BinaryLibraryRecord.Decode(reader, options),
            SerializationRecordType.BinaryObjectString => BinaryObjectStringRecord.Decode(reader),
            SerializationRecordType.ClassWithId => ClassWithIdRecord.Decode(reader, recordMap),
            SerializationRecordType.ClassWithMembersAndTypes => ClassWithMembersAndTypesRecord.Decode(reader, recordMap, options),
            SerializationRecordType.MemberPrimitiveTyped => DecodeMemberPrimitiveTypedRecord(reader),
            SerializationRecordType.MemberReference => MemberReferenceRecord.Decode(reader, recordMap),
            SerializationRecordType.MessageEnd => MessageEndRecord.Singleton,
            SerializationRecordType.ObjectNull => ObjectNullRecord.Instance,
            SerializationRecordType.ObjectNullMultiple => ObjectNullMultipleRecord.Decode(reader),
            SerializationRecordType.ObjectNullMultiple256 => ObjectNullMultiple256Record.Decode(reader),
            SerializationRecordType.SerializedStreamHeader => SerializedStreamHeaderRecord.Decode(reader),
            SerializationRecordType.SystemClassWithMembersAndTypes => SystemClassWithMembersAndTypesRecord.Decode(reader, recordMap, options),
            _ => throw new InvalidOperationException()
        };

        recordMap.Add(record);

        return record;
    }

    private static SerializationRecord DecodeMemberPrimitiveTypedRecord(BinaryReader reader)
    {
        PrimitiveType primitiveType = reader.ReadPrimitiveType();

        return primitiveType switch
        {
            PrimitiveType.Boolean => new MemberPrimitiveTypedRecord<bool>(reader.ReadBoolean()),
            PrimitiveType.Byte => new MemberPrimitiveTypedRecord<byte>(reader.ReadByte()),
            PrimitiveType.SByte => new MemberPrimitiveTypedRecord<sbyte>(reader.ReadSByte()),
            PrimitiveType.Char => new MemberPrimitiveTypedRecord<char>(reader.ParseChar()),
            PrimitiveType.Int16 => new MemberPrimitiveTypedRecord<short>(reader.ReadInt16()),
            PrimitiveType.UInt16 => new MemberPrimitiveTypedRecord<ushort>(reader.ReadUInt16()),
            PrimitiveType.Int32 => new MemberPrimitiveTypedRecord<int>(reader.ReadInt32()),
            PrimitiveType.UInt32 => new MemberPrimitiveTypedRecord<uint>(reader.ReadUInt32()),
            PrimitiveType.Int64 => new MemberPrimitiveTypedRecord<long>(reader.ReadInt64()),
            PrimitiveType.UInt64 => new MemberPrimitiveTypedRecord<ulong>(reader.ReadUInt64()),
            PrimitiveType.Single => new MemberPrimitiveTypedRecord<float>(reader.ReadSingle()),
            PrimitiveType.Double => new MemberPrimitiveTypedRecord<double>(reader.ReadDouble()),
            PrimitiveType.Decimal => new MemberPrimitiveTypedRecord<decimal>(reader.ParseDecimal()),
            PrimitiveType.DateTime => new MemberPrimitiveTypedRecord<DateTime>(Utils.BinaryReaderExtensions.CreateDateTimeFromData(reader.ReadUInt64())),
            PrimitiveType.TimeSpan => new MemberPrimitiveTypedRecord<TimeSpan>(new TimeSpan(reader.ReadInt64())),
            _ => throw new InvalidOperationException()
        };
    }

    private static ArrayRecord DecodeArraySinglePrimitiveRecord(BinaryReader reader)
    {
        ArrayInfo info = ArrayInfo.Decode(reader);
        PrimitiveType primitiveType = reader.ReadPrimitiveType();

        return DecodeArraySinglePrimitiveRecord(reader, info, primitiveType);
    }

    private static ArrayRecord DecodeArraySinglePrimitiveRecord(BinaryReader reader, ArrayInfo info, PrimitiveType primitiveType)
    {
        return primitiveType switch
        {
            PrimitiveType.Boolean => Decode<bool>(info, reader),
            PrimitiveType.Byte => Decode<byte>(info, reader),
            PrimitiveType.SByte => Decode<sbyte>(info, reader),
            PrimitiveType.Char => Decode<char>(info, reader),
            PrimitiveType.Int16 => Decode<short>(info, reader),
            PrimitiveType.UInt16 => Decode<ushort>(info, reader),
            PrimitiveType.Int32 => Decode<int>(info, reader),
            PrimitiveType.UInt32 => Decode<uint>(info, reader),
            PrimitiveType.Int64 => Decode<long>(info, reader),
            PrimitiveType.UInt64 => Decode<ulong>(info, reader),
            PrimitiveType.Single => Decode<float>(info, reader),
            PrimitiveType.Double => Decode<double>(info, reader),
            PrimitiveType.Decimal => Decode<decimal>(info, reader),
            PrimitiveType.DateTime => Decode<DateTime>(info, reader),
            PrimitiveType.TimeSpan => Decode<TimeSpan>(info, reader),
            _ => throw new InvalidOperationException()
        };

        static ArrayRecord Decode<T>(ArrayInfo info, BinaryReader reader) where T : unmanaged
            => new ArraySinglePrimitiveRecord<T>(info, ArraySinglePrimitiveRecord<T>.DecodePrimitiveTypes(reader, info.GetSZArrayLength()));
    }

    private static ArrayRecord DecodeArrayRectangularPrimitiveRecord(PrimitiveType primitiveType, ArrayInfo info, int[] lengths, BinaryReader reader)
    {
        return primitiveType switch
        {
            PrimitiveType.Boolean => Decode<bool>(info, lengths, reader),
            PrimitiveType.Byte => Decode<byte>(info, lengths, reader),
            PrimitiveType.SByte => Decode<sbyte>(info, lengths, reader),
            PrimitiveType.Char => Decode<char>(info, lengths, reader),
            PrimitiveType.Int16 => Decode<short>(info, lengths, reader),
            PrimitiveType.UInt16 => Decode<ushort>(info, lengths, reader),
            PrimitiveType.Int32 => Decode<int>(info, lengths, reader),
            PrimitiveType.UInt32 => Decode<uint>(info, lengths, reader),
            PrimitiveType.Int64 => Decode<long>(info, lengths, reader),
            PrimitiveType.UInt64 => Decode<ulong>(info, lengths, reader),
            PrimitiveType.Single => Decode<float>(info, lengths, reader),
            PrimitiveType.Double => Decode<double>(info, lengths, reader),
            PrimitiveType.Decimal => Decode<decimal>(info, lengths, reader),
            PrimitiveType.DateTime => Decode<DateTime>(info, lengths, reader),
            PrimitiveType.TimeSpan => Decode<TimeSpan>(info, lengths, reader),
            _ => throw new InvalidOperationException()
        };

        static ArrayRecord Decode<T>(ArrayInfo info, int[] lengths, BinaryReader reader) where T : unmanaged
        {
            // We limit the length of multi-dimensional array to max length of SZArray.
            // Because of that, it's possible to re-use the same decoding logic for both MD and SZ arrays.
            IReadOnlyList<T> values = ArraySinglePrimitiveRecord<T>.DecodePrimitiveTypes(reader, info.GetSZArrayLength());
            return new ArrayRectangularPrimitiveRecord<T>(info, lengths, values);
        }
    }

    private static ArrayRecord DecodeBinaryArrayRecord(BinaryReader reader, RecordMap recordMap, PayloadOptions options)
    {
        SerializationRecordId objectId = SerializationRecordId.Decode(reader);
        BinaryArrayType arrayType = reader.ReadArrayType();
        int rank = reader.ReadInt32();

        bool isRectangular = arrayType is BinaryArrayType.Rectangular;

        // It is an arbitrary limit in the current CoreCLR type loader.
        // Don't change this value without reviewing the loop a few lines below.
        const int MaxSupportedArrayRank = 32;

        if (rank < 1 || rank > MaxSupportedArrayRank
            || (rank != 1 && !isRectangular)
            || (rank == 1 && isRectangular))
        {
            ThrowHelper.ThrowInvalidValue(rank);
        }

        int[] lengths = new int[rank]; // adversary-controlled, but acceptable since upper limit of 32
        long totalElementCount = 1; // to avoid integer overflow during the multiplication below
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = ArrayInfo.ParseValidArrayLength(reader);
            totalElementCount *= lengths[i];

            // n.b. This forbids "new T[Array.MaxLength, Array.MaxLength, Array.MaxLength, ..., 0]"
            // but allows "new T[0, Array.MaxLength, Array.MaxLength, Array.MaxLength, ...]". But
            // that's the same behavior that newarr and Array.CreateInstance exhibit, so at least
            // we're consistent.

            if (totalElementCount > ArrayInfo.MaxArrayLength)
            {
                ThrowHelper.ThrowInvalidValue(lengths[i]); // max array size exceeded
            }
        }

        // Per BinaryReaderExtensions.ReadArrayType, we do not support nonzero offsets, so
        // we don't need to read the NRBF stream 'LowerBounds' field here.

        MemberTypeInfo memberTypeInfo = MemberTypeInfo.Decode(reader, 1, options, recordMap);
        ArrayInfo arrayInfo = new(objectId, totalElementCount, arrayType, rank);

        (BinaryType binaryType, object? additionalInfo) = memberTypeInfo.Infos[0];
        if (arrayType == BinaryArrayType.Rectangular)
        {
            if (binaryType == BinaryType.Primitive)
            {
                return DecodeArrayRectangularPrimitiveRecord((PrimitiveType)additionalInfo!, arrayInfo, lengths, reader);
            }
            else if (binaryType == BinaryType.String)
            {
                return new RectangularArrayRecord(typeof(string), arrayInfo, memberTypeInfo, lengths);
            }
            else if (binaryType == BinaryType.Object)
            {
                return new RectangularArrayRecord(typeof(SerializationRecord), arrayInfo, memberTypeInfo, lengths);
            }
            else if (binaryType is BinaryType.SystemClass or BinaryType.Class)
            {
                TypeName typeName = binaryType == BinaryType.SystemClass ? (TypeName)additionalInfo! : ((ClassTypeInfo)additionalInfo!).TypeName;
                // BinaryArrayType.Rectangular can be also a jagged array.
                return typeName.IsArray
                    ? new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths)
                    : new RectangularArrayRecord(typeof(SerializationRecord), arrayInfo, memberTypeInfo, lengths);
            }
            else if (binaryType is BinaryType.PrimitiveArray or BinaryType.StringArray or BinaryType.ObjectArray)
            {
                // A multi-dimensional array of single dimensional arrays. Example: int[][,]
                return new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths);
            }
        }
        else if (arrayType == BinaryArrayType.Single)
        {
            if (binaryType is BinaryType.SystemClass or BinaryType.Class)
            {
                TypeName typeName = binaryType == BinaryType.SystemClass ? (TypeName)additionalInfo! : ((ClassTypeInfo)additionalInfo!).TypeName;
                // BinaryArrayType.Single that describes an array is just a jagged array.
                return typeName.IsArray
                    ? new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths)
                    : new SZArrayOfRecords(arrayInfo, memberTypeInfo);
            }
            else if (binaryType == BinaryType.String)
            {
                // BinaryArrayRecord can represent string[] (but BF always uses ArraySingleStringRecord for that).
                return new ArraySingleStringRecord(arrayInfo);
            }
            else if (binaryType == BinaryType.Primitive)
            {
                // BinaryArrayRecord can represent Primitive[] (but BF always uses ArraySinglePrimitiveRecord for that).
                return DecodeArraySinglePrimitiveRecord(reader, arrayInfo, (PrimitiveType)additionalInfo!);
            }
            else if (binaryType == BinaryType.Object)
            {
                // BinaryArrayRecord can represent object[] (but BF always uses ArraySingleObjectRecord for that).
                return new ArraySingleObjectRecord(arrayInfo);
            }
            else if (binaryType is BinaryType.ObjectArray or BinaryType.StringArray or BinaryType.PrimitiveArray)
            {
                // It's a Jagged array that does not use BinaryArrayType.Jagged.
                return new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths);
            }
        }
        else if (arrayType == BinaryArrayType.Jagged)
        {
            if (binaryType is BinaryType.ObjectArray or BinaryType.StringArray or BinaryType.PrimitiveArray)
            {
                // It's a Jagged array that does not use BinaryArrayType.Jagged.
                return new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths);
            }
            else if (binaryType == BinaryType.SystemClass && ((TypeName)additionalInfo!).IsArray)
            {
                // BinaryType.SystemClass can be used to describe arrays of system class records.
                // Example: new Exception[] { new Exception("test") };
                return new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths);
            }
            else if (binaryType == BinaryType.Class && ((ClassTypeInfo)additionalInfo!).TypeName.IsArray)
            {
                // BinaryType.Class can be used to describe arrays of class records.
                // Example: new MyCustomType[] { new MyCustomType(0) };
                return new JaggedArrayRecord(arrayInfo, memberTypeInfo, lengths);
            }

            // It's invalid, the element type must be an array.
            throw new SerializationException(SR.Format(SR.Serialization_InvalidValue, binaryType));
        }

        throw new InvalidOperationException();
    }

    /// <summary>
    /// This method is responsible for pushing only the FIRST read info
    /// of the NESTED record into the <paramref name="readStack"/>.
    /// It's not pushing all of them, because it could be used as a vector of attack.
    /// Example: BinaryArrayRecord with Array.MaxLength length,
    /// where first item turns out to be <see cref="ObjectNullMultipleRecord"/>
    /// that provides Array.MaxLength nulls.
    /// </summary>
    private static void PushFirstNestedRecordInfo(SerializationRecord record, Stack<NextInfo> readStack)
    {
        if (record is ClassRecord classRecord)
        {
            if (classRecord.ExpectedValuesCount > 0)
            {
                (AllowedRecordTypes allowed, PrimitiveType primitiveType) = classRecord.GetNextAllowedRecordType();

                readStack.Push(new(allowed, classRecord, readStack, primitiveType));
            }
        }
        else if (record is ArrayRecord arrayRecord && arrayRecord.ValuesToRead > 0)
        {
            (AllowedRecordTypes allowed, PrimitiveType primitiveType) = arrayRecord.GetAllowedRecordType();

            readStack.Push(new(allowed, arrayRecord, readStack, primitiveType));
        }
    }
}
