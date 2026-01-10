namespace System.Network.IORing;

/// <summary>
/// Interface for architecture-specific Linux io_uring syscalls.
/// </summary>
public interface ILinuxArch
{
    int io_uring_setup(uint entries, ref io_uring_params p);
    int io_uring_enter(int fd, uint to_submit, uint min_complete, uint flags);
    int io_uring_register(int fd, uint opcode, nint arg, uint nr_args);
    nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);
    int munmap(nint addr, nuint length);
    int close(int fd);

    int PROT_READ { get; }
    int PROT_WRITE { get; }
    int MAP_SHARED { get; }
    int MAP_POPULATE { get; }

    ulong IORING_OFF_SQ_RING { get; }
    ulong IORING_OFF_CQ_RING { get; }
    ulong IORING_OFF_SQES { get; }
}
