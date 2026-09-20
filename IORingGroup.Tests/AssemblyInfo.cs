// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using Xunit;

// IORingBuffer.ForceLegacyWindowsPath is a process-wide seam: a test that toggles it while another
// class maps a sub-64 KiB buffer makes that buffer fail validation. The suite is sub-second, so
// running classes one at a time costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
