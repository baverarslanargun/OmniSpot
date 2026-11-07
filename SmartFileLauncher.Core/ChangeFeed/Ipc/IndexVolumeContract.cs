namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed record IndexJournalPositionDto(ulong JournalId, long NextUsn);

public sealed record IndexVolumePageDto(
    string RootPath,
    ulong RootReference,
    ulong VolumeSerial,
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    string? Token = null,
    string? Entries = null,
    string? States = null,
    int Count = 0,
    bool Done = false,
    bool RescanRequired = false);
