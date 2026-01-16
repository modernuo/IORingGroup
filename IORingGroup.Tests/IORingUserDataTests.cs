// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Network;
using Xunit;

namespace IORingGroup.Tests;

public class IORingUserDataTests
{
    [Theory]
    [InlineData(IORingUserData.OpAccept, 0, (ushort)0)]
    [InlineData(IORingUserData.OpRecv, 0, (ushort)0)]
    [InlineData(IORingUserData.OpSend, 0, (ushort)0)]
    [InlineData(IORingUserData.OpRecv, 1, (ushort)1)]
    [InlineData(IORingUserData.OpSend, 4095, (ushort)65535)]
    [InlineData(IORingUserData.OpRecv, int.MaxValue, (ushort)12345)]
    public void Encode_Decode_RoundTrip(int opType, int socketId, ushort generation)
    {
        // Arrange & Act
        var encoded = IORingUserData.Encode(opType, socketId, generation);
        var (decodedOpType, decodedSocketId, decodedGeneration) = IORingUserData.Decode(encoded);

        // Assert
        Assert.Equal(opType, decodedOpType);
        Assert.Equal(socketId, decodedSocketId);
        Assert.Equal(generation, decodedGeneration);
    }

    [Fact]
    public void Encode_DifferentOpTypes_ProduceDifferentValues()
    {
        // Arrange
        const int socketId = 100;
        const ushort generation = 1;

        // Act
        var acceptData = IORingUserData.Encode(IORingUserData.OpAccept, socketId, generation);
        var recvData = IORingUserData.Encode(IORingUserData.OpRecv, socketId, generation);
        var sendData = IORingUserData.Encode(IORingUserData.OpSend, socketId, generation);

        // Assert
        Assert.NotEqual(acceptData, recvData);
        Assert.NotEqual(recvData, sendData);
        Assert.NotEqual(acceptData, sendData);
    }

    [Fact]
    public void Encode_DifferentGenerations_ProduceDifferentValues()
    {
        // Arrange
        const int opType = IORingUserData.OpRecv;
        const int socketId = 100;

        // Act
        var gen0 = IORingUserData.Encode(opType, socketId, 0);
        var gen1 = IORingUserData.Encode(opType, socketId, 1);
        var genMax = IORingUserData.Encode(opType, socketId, ushort.MaxValue);

        // Assert
        Assert.NotEqual(gen0, gen1);
        Assert.NotEqual(gen1, genMax);
    }

    [Fact]
    public void GetOpType_ReturnsCorrectOpType()
    {
        // Arrange
        var recvData = IORingUserData.Encode(IORingUserData.OpRecv, 42, 123);
        var sendData = IORingUserData.Encode(IORingUserData.OpSend, 42, 123);

        // Act & Assert
        Assert.Equal(IORingUserData.OpRecv, IORingUserData.GetOpType(recvData));
        Assert.Equal(IORingUserData.OpSend, IORingUserData.GetOpType(sendData));
    }

    [Fact]
    public void GetSocketId_ReturnsCorrectSocketId()
    {
        // Arrange
        var data1 = IORingUserData.Encode(IORingUserData.OpRecv, 0, 1);
        var data2 = IORingUserData.Encode(IORingUserData.OpRecv, 4095, 1);
        var data3 = IORingUserData.Encode(IORingUserData.OpRecv, int.MaxValue, 1);

        // Act & Assert
        Assert.Equal(0, IORingUserData.GetSocketId(data1));
        Assert.Equal(4095, IORingUserData.GetSocketId(data2));
        Assert.Equal(int.MaxValue, IORingUserData.GetSocketId(data3));
    }

    [Fact]
    public void GetGeneration_ReturnsCorrectGeneration()
    {
        // Arrange
        var data1 = IORingUserData.Encode(IORingUserData.OpRecv, 42, 0);
        var data2 = IORingUserData.Encode(IORingUserData.OpRecv, 42, 1);
        var data3 = IORingUserData.Encode(IORingUserData.OpRecv, 42, ushort.MaxValue);

        // Act & Assert
        Assert.Equal((ushort)0, IORingUserData.GetGeneration(data1));
        Assert.Equal((ushort)1, IORingUserData.GetGeneration(data2));
        Assert.Equal(ushort.MaxValue, IORingUserData.GetGeneration(data3));
    }

    [Fact]
    public void IsCurrentGeneration_ReturnsTrueForMatchingGeneration()
    {
        // Arrange
        const ushort generation = 42;
        var data = IORingUserData.Encode(IORingUserData.OpRecv, 100, generation);

        // Act & Assert
        Assert.True(IORingUserData.IsCurrentGeneration(data, generation));
    }

    [Fact]
    public void IsCurrentGeneration_ReturnsFalseForMismatchedGeneration()
    {
        // Arrange
        var data = IORingUserData.Encode(IORingUserData.OpRecv, 100, 42);

        // Act & Assert
        Assert.False(IORingUserData.IsCurrentGeneration(data, 41));
        Assert.False(IORingUserData.IsCurrentGeneration(data, 43));
        Assert.False(IORingUserData.IsCurrentGeneration(data, 0));
    }

    [Fact]
    public void EncodeAccept_CreatesValidAcceptUserData()
    {
        // Arrange & Act
        var acceptData = IORingUserData.EncodeAccept();
        var (opType, socketId, generation) = IORingUserData.Decode(acceptData);

        // Assert
        Assert.Equal(IORingUserData.OpAccept, opType);
        Assert.Equal(0, socketId);
        Assert.Equal((ushort)0, generation);
    }

    [Fact]
    public void EncodeAccept_WithListenerIndex_PreservesIndex()
    {
        // Arrange & Act
        var acceptData = IORingUserData.EncodeAccept(listenerIndex: 5);
        var (opType, socketId, _) = IORingUserData.Decode(acceptData);

        // Assert
        Assert.Equal(IORingUserData.OpAccept, opType);
        Assert.Equal(5, socketId); // listenerIndex stored in socketId field
    }

    [Fact]
    public void GenerationWraparound_WorksCorrectly()
    {
        // Test that generation counter wrapping works
        const int socketId = 100;

        // Max generation
        var dataMax = IORingUserData.Encode(IORingUserData.OpRecv, socketId, ushort.MaxValue);
        Assert.Equal(ushort.MaxValue, IORingUserData.GetGeneration(dataMax));

        // Wrapped to 0
        var data0 = IORingUserData.Encode(IORingUserData.OpRecv, socketId, 0);
        Assert.Equal((ushort)0, IORingUserData.GetGeneration(data0));

        // Different generations should not match
        Assert.False(IORingUserData.IsCurrentGeneration(dataMax, 0));
        Assert.False(IORingUserData.IsCurrentGeneration(data0, ushort.MaxValue));
    }

    [Fact]
    public void NegativeSocketId_HandledCorrectly()
    {
        // Negative socket IDs shouldn't be used, but test behavior
        var data = IORingUserData.Encode(IORingUserData.OpRecv, -1, 1);
        var (_, socketId, _) = IORingUserData.Decode(data);

        // -1 as int becomes uint.MaxValue when masked
        Assert.Equal(-1, socketId);
    }
}
