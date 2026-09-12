// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026, ModernUO

using System.Network;

namespace IORingGroup.Tests;

/// <summary>
/// Forwards to a real ring but starts refusing <see cref="RegisterBuffer"/> once armed, so a lazily
/// filled pool fails while creating a slab the way a full registration table or an rlimit would.
/// </summary>
public sealed class FailingRegistrationRing : IIORingGroup
{
    private readonly IIORingGroup _inner;
    private int _allowedRegistrations = int.MaxValue;
    private int _registrationsBeforeArgumentError = int.MaxValue;

    public FailingRegistrationRing(IIORingGroup inner) => _inner = inner;

    /// <summary>Lets the next <paramref name="count"/> registrations through, then refuses every one after.</summary>
    public void FailRegistrationsAfter(int count) => _allowedRegistrations = count;

    /// <summary>Registrations refused so far.</summary>
    public int RefusedRegistrations { get; private set; }

    /// <summary>Makes <see cref="RegisterBuffer"/> raise an argument error instead of refusing operationally.</summary>
    public bool ThrowArgumentExceptionOnRegister { get; set; }

    /// <summary>
    /// Lets the next <paramref name="count"/> registrations through, then raises an argument error;
    /// <see cref="int.MaxValue"/> disarms it. Puts the failure inside one specific slab.
    /// </summary>
    public void ThrowArgumentExceptionAfter(int count) => _registrationsBeforeArgumentError = count;

    public int RegisterBuffer(IORingBuffer buffer)
    {
        if (ThrowArgumentExceptionOnRegister || _registrationsBeforeArgumentError <= 0)
        {
            throw new ArgumentException("Test-injected argument error", nameof(buffer));
        }

        if (_registrationsBeforeArgumentError != int.MaxValue)
        {
            _registrationsBeforeArgumentError--;
        }

        if (_allowedRegistrations <= 0)
        {
            RefusedRegistrations++;
            return -1; // The pool turns this into InvalidOperationException, as a real ring would
        }

        _allowedRegistrations--;
        return _inner.RegisterBuffer(buffer);
    }

    public int SubmissionQueueSpace => _inner.SubmissionQueueSpace;
    public int CompletionQueueCount => _inner.CompletionQueueCount;
    public int MaxOutstandingSendsPerSocket => _inner.MaxOutstandingSendsPerSocket;
    public int MaxRegisteredBuffers => _inner.MaxRegisteredBuffers;
    public bool CloseCancelsPendingIo => _inner.CloseCancelsPendingIo;
    public bool SupportsHighResolutionWait => _inner.SupportsHighResolutionWait;

    public void PreparePollAdd(nint fd, PollMask mask, ulong userData) => _inner.PreparePollAdd(fd, mask, userData);
    public void PreparePollRemove(ulong userData) => _inner.PreparePollRemove(userData);
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData) =>
        _inner.PrepareAccept(listenFd, addr, addrLen, userData);
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData) =>
        _inner.PrepareConnect(fd, addr, addrLen, userData);
    public void PrepareClose(nint fd, ulong userData) => _inner.PrepareClose(fd, userData);
    public void PrepareCancel(ulong targetUserData, ulong userData) => _inner.PrepareCancel(targetUserData, userData);
    public void PrepareShutdown(nint fd, int how, ulong userData) => _inner.PrepareShutdown(fd, how, userData);
    public int Submit() => _inner.Submit();
    public int SubmitAndWait(int waitNr) => _inner.SubmitAndWait(waitNr);
    public int PeekCompletions(Span<Completion> completions) => _inner.PeekCompletions(completions);
    public void AdvanceCompletionQueue(int count) => _inner.AdvanceCompletionQueue(count);
    public nint CreateListener(string bindAddress, ushort port, int backlog) =>
        _inner.CreateListener(bindAddress, port, backlog);
    public void CloseListener(nint listener) => _inner.CloseListener(listener);
    public void ConfigureSocket(nint socket) => _inner.ConfigureSocket(socket);
    public int RegisterSocket(nint socket) => _inner.RegisterSocket(socket);
    public void UnregisterSocket(int connId) => _inner.UnregisterSocket(connId);
    public void CloseSocket(nint socket) => _inner.CloseSocket(socket);
    public void Shutdown(nint socket, int how) => _inner.Shutdown(socket, how);
    public void UnregisterBuffer(int bufferId) => _inner.UnregisterBuffer(bufferId);
    public void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData) =>
        _inner.PrepareSendBuffer(connId, bufferId, offset, length, userData);
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData) =>
        _inner.PrepareRecvBuffer(connId, bufferId, offset, length, userData);
    public void WaitForCompletion(int timeoutMs) => _inner.WaitForCompletion(timeoutMs);
    public void Wake() => _inner.Wake();
    public void Dispose() => _inner.Dispose();
}
