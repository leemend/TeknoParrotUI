#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <shellapi.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <climits>

#pragma comment(lib, "shell32.lib")

static uintptr_t g_existingHookTarget = 0;

constexpr uintptr_t STARTUP_PLAYER_HOOK = 0x08254CA3;
constexpr uintptr_t STARTUP_PLAYER_RETURN = 0x08254CA9;

constexpr uintptr_t PLAYER_NAME_BASE = 0x089CF364;
constexpr uintptr_t PLAYER_NAME_STRIDE = 0x104;
constexpr size_t PLAYER_INITIALS_LENGTH = 3;

static volatile LONG g_running = 1;
static volatile LONG g_initialsPending[4] = {};

constexpr int MAX_CANDIDATES = 512;
constexpr int APPLY_COUNT = 1;
constexpr DWORD APPLY_INTERVAL_MS = 0;

struct GolferCandidate
{
    volatile LONG valid;
    void* golfer;
    void* caller;
    int resolvedPlayer;
    int applyCount;
    ULONGLONG nextApplyTick;
};

static GolferCandidate g_candidates[MAX_CANDIDATES] = {};
static volatile LONG g_candidateSequence = 0;

static wchar_t g_iniPath[MAX_PATH * 4] = {};

struct PlayerConfig
{
    bool overrideOutfit = false;
    bool overrideInitials = false;
    bool female = false;

    char initials[PLAYER_INITIALS_LENGTH + 1] = {};

    int face = 0;
    int shirt = 0;
    int bottoms = 0;
    int shoes = 0;
    int hat = 0;
    int bodysuit = 0;

    int clubs = 0;
    int balls = 0;
};

static PlayerConfig g_config[4];

static void AppendLog(const char* text)
{
    HANDLE file = CreateFileA(
        "C:\\TeknoParrot\\GTPlayerDefaults-hook.txt",
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (file == INVALID_HANDLE_VALUE)
        return;

    DWORD written = 0;

    WriteFile(
        file,
        text,
        static_cast<DWORD>(strlen(text)),
        &written,
        nullptr);

    CloseHandle(file);
}

static void LogWidePath(
    const char* prefix,
    const wchar_t* path)
{
    char utf8[2048] = {};

    WideCharToMultiByte(
        CP_UTF8,
        0,
        path,
        -1,
        utf8,
        static_cast<int>(sizeof(utf8)),
        nullptr,
        nullptr);

    char buffer[2300] = {};

    sprintf_s(
        buffer,
        "%s%s\r\n",
        prefix,
        utf8);

    AppendLog(buffer);
}

static bool EndsWithInsensitive(
    const wchar_t* text,
    const wchar_t* suffix)
{
    if (!text || !suffix)
        return false;

    size_t textLength = wcslen(text);
    size_t suffixLength = wcslen(suffix);

    if (suffixLength > textLength)
        return false;

    return _wcsicmp(
        text + textLength - suffixLength,
        suffix) == 0;
}

static bool ResolveIniPath()
{
    const wchar_t* commandLine = GetCommandLineW();

    if (!commandLine)
        return false;

    int argc = 0;

    LPWSTR* argv =
        CommandLineToArgvW(
            commandLine,
            &argc);

    if (!argv)
        return false;

    wchar_t gamePath[MAX_PATH * 4] = {};

    for (int i = 0; i < argc; i++)
    {
        if (EndsWithInsensitive(
                argv[i],
                L"game.bin"))
        {
            wcsncpy_s(
                gamePath,
                argv[i],
                _TRUNCATE);

            break;
        }
    }

    LocalFree(argv);

    if (gamePath[0] == L'\0')
    {
        AppendLog(
            "[CONFIG ERROR] Could not find game.bin.\r\n");

        return false;
    }

    LogWidePath(
        "[CONFIG] game.bin = ",
        gamePath);

    wchar_t* slash1 = wcsrchr(gamePath, L'\\');
    wchar_t* slash2 = wcsrchr(gamePath, L'/');
    wchar_t* slash = slash1;

    if (!slash ||
        (slash2 && slash2 > slash))
    {
        slash = slash2;
    }

    if (!slash)
        return false;

    *slash = L'\0';

    swprintf_s(
        g_iniPath,
        L"%s\\teknoparrot.ini",
        gamePath);

    LogWidePath(
        "[CONFIG] ini      = ",
        g_iniPath);

    DWORD attributes =
        GetFileAttributesW(
            g_iniPath);

    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (attributes & FILE_ATTRIBUTE_DIRECTORY))
    {
        AppendLog(
            "[CONFIG ERROR] teknoparrot.ini not found.\r\n");

        return false;
    }

    AppendLog(
        "[CONFIG] teknoparrot.ini found.\r\n");

    return true;
}

static int ReadIniInt(
    const wchar_t* section,
    const wchar_t* key,
    int defaultValue = 0)
{
    return static_cast<int>(
        GetPrivateProfileIntW(
            section,
            key,
            defaultValue,
            g_iniPath));
}

static void ReadIniString(
    const wchar_t* section,
    const wchar_t* key,
    wchar_t* output,
    DWORD outputSize,
    const wchar_t* defaultValue = L"")
{
    GetPrivateProfileStringW(
        section,
        key,
        defaultValue,
        output,
        outputSize,
        g_iniPath);
}

static void ReadPlayerConfig(
    int playerNumber,
    PlayerConfig& config)
{
    wchar_t section[128] = {};
    wchar_t key[128] = {};

    swprintf_s(
        section,
        L"Player %d Customization",
        playerNumber);

    swprintf_s(
        key,
        L"P%d Override Default Outfit",
        playerNumber);

    config.overrideOutfit =
        ReadIniInt(section, key, 0) != 0;

    swprintf_s(
        key,
        L"P%d Default Gender",
        playerNumber);

    wchar_t gender[32] = {};

    ReadIniString(
        section,
        key,
        gender,
        static_cast<DWORD>(
            sizeof(gender) / sizeof(gender[0])),
        L"Male");

    config.female =
        _wcsicmp(gender, L"Female") == 0;

    swprintf_s(
        key,
        L"P%d Override Default Initials",
        playerNumber);

    config.overrideInitials =
        ReadIniInt(section, key, 0) != 0;

    wchar_t initialsWide[32] = {};

    swprintf_s(
        key,
        L"P%d Default Initials",
        playerNumber);

    ReadIniString(
        section,
        key,
        initialsWide,
        static_cast<DWORD>(
            sizeof(initialsWide) / sizeof(initialsWide[0])),
        L"");

    char initialsAscii[32] = {};

    WideCharToMultiByte(
        CP_ACP,
        0,
        initialsWide,
        -1,
        initialsAscii,
        static_cast<int>(sizeof(initialsAscii)),
        nullptr,
        nullptr);

    strncpy_s(
        config.initials,
        initialsAscii,
        PLAYER_INITIALS_LENGTH);

    config.initials[PLAYER_INITIALS_LENGTH] = '\0';

    swprintf_s(key, L"P%d Default Face", playerNumber);
    config.face = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Shirt", playerNumber);
    config.shirt = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Bottoms", playerNumber);
    config.bottoms = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Shoes", playerNumber);
    config.shoes = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Hat", playerNumber);
    config.hat = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Bodysuit", playerNumber);
    config.bodysuit = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Clubs", playerNumber);
    config.clubs = ReadIniInt(section, key);

    swprintf_s(key, L"P%d Default Balls", playerNumber);
    config.balls = ReadIniInt(section, key);

    char initialsLog[192] = {};

    sprintf_s(
        initialsLog,
        "[CONFIG INITIALS] P%d override=%d initials=%.3s\r\n",
        playerNumber,
        config.overrideInitials ? 1 : 0,
        config.initials);

    AppendLog(initialsLog);

    char buffer[512] = {};

    sprintf_s(
        buffer,
        "[CONFIG] P%d override=%d gender=%s "
        "face=%d shirt=%d bottoms=%d shoes=%d "
        "hat=%d bodysuit=%d clubs=%d balls=%d\r\n",
        playerNumber,
        config.overrideOutfit ? 1 : 0,
        config.female ? "Female" : "Male",
        config.face,
        config.shirt,
        config.bottoms,
        config.shoes,
        config.hat,
        config.bodysuit,
        config.clubs,
        config.balls);

    AppendLog(buffer);
}

static bool LoadConfiguration()
{
    if (!ResolveIniPath())
        return false;

    ReadPlayerConfig(2, g_config[1]);
    ReadPlayerConfig(3, g_config[2]);
    ReadPlayerConfig(4, g_config[3]);

    return true;
}

static int GetPlayerIndex(
    void* golfer,
    uint32_t* slotOut)
{
    if (slotOut)
        *slotOut = 0;

    if (!golfer)
        return -1;

    __try
    {
        auto* g =
            reinterpret_cast<uint8_t*>(golfer);

        auto* node =
            *reinterpret_cast<uint8_t**>(
                g - 0x10);

        if (!node)
            return -1;

        auto* holder =
            node + 0x20;

        uint32_t slot =
            *reinterpret_cast<uint32_t*>(
                holder + 0x04);

        if (slotOut)
            *slotOut = slot;

        switch (slot & 0xFFF)
        {
            case 0x960: return 0;
            case 0xB60: return 1;
            case 0xD60: return 2;
            case 0xF60: return 3;
            default:    return -1;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return -1;
    }
}

static uint32_t ToZeroBased(
    int uiValue)
{
    if (uiValue <= 0)
        return 0;

    return static_cast<uint32_t>(
        uiValue - 1);
}

static bool ApplyPlayerInitials(
    int playerIndex,
    const PlayerConfig& config)
{
    if (playerIndex <= 0 ||
        playerIndex > 3 ||
        !config.overrideInitials ||
        config.initials[0] == '\0')
    {
        return false;
    }

    __try
    {
        auto* destination =
            reinterpret_cast<char*>(
                PLAYER_NAME_BASE +
                (static_cast<uintptr_t>(playerIndex) *
                 PLAYER_NAME_STRIDE));

        char before[PLAYER_INITIALS_LENGTH + 1] = {};

        memcpy(
            before,
            destination,
            PLAYER_INITIALS_LENGTH);

        before[PLAYER_INITIALS_LENGTH] = '\0';

        memset(
            destination,
            0,
            PLAYER_INITIALS_LENGTH + 1);

        memcpy(
            destination,
            config.initials,
            strnlen_s(
                config.initials,
                PLAYER_INITIALS_LENGTH));

        char buffer[256] = {};

        sprintf_s(
            buffer,
            "[INITIALS] P%d %.3s -> %.3s @ 0x%08X\r\n",
            playerIndex + 1,
            before,
            destination,
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    destination)));

        AppendLog(buffer);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        char buffer[192] = {};

        sprintf_s(
            buffer,
            "[INITIALS ERROR] P%d target=0x%08X\r\n",
            playerIndex + 1,
            static_cast<unsigned>(
                PLAYER_NAME_BASE +
                (static_cast<uintptr_t>(playerIndex) *
                 PLAYER_NAME_STRIDE)));

        AppendLog(buffer);
        return false;
    }
}

static void QueuePlayerInitials(
    int playerIndex)
{
    if (playerIndex <= 0 ||
        playerIndex > 3)
    {
        return;
    }

    PlayerConfig& config =
        g_config[playerIndex];

    if (!config.overrideInitials ||
        config.initials[0] == '\0')
    {
        return;
    }

    InterlockedExchange(
        &g_initialsPending[playerIndex],
        1);
}

static DWORD WINAPI InitialsWriterThread(
    LPVOID)
{
    AppendLog(
        "[INITIALS] Separate initials writer started.\r\n");

    while (InterlockedCompareExchange(
               &g_running,
               1,
               1))
    {
        for (int playerIndex = 1;
             playerIndex <= 3;
             playerIndex++)
        {
            if (InterlockedExchange(
                    &g_initialsPending[playerIndex],
                    0) != 1)
            {
                continue;
            }

            // The game can initialize this name table after the startup hook.
            // Reapply briefly so the configured initials survive startup.
            for (int attempt = 0;
                 attempt < 8 &&
                 InterlockedCompareExchange(&g_running, 1, 1);
                 attempt++)
            {
                Sleep(attempt == 0 ? 50 : 250);

                ApplyPlayerInitials(
                    playerIndex,
                    g_config[playerIndex]);
            }
        }

        Sleep(1);
    }

    return 0;
}
static void WriteField(
    uint8_t* golfer,
    size_t offset,
    uint32_t value)
{
    *reinterpret_cast<uint32_t*>(
        golfer + offset) = value;
}

static uint32_t ReadField(
    uint8_t* golfer,
    size_t offset)
{
    return *reinterpret_cast<uint32_t*>(
        golfer + offset);
}

static bool ApplyPlayerConfig(
    int playerIndex,
    void* golfer,
    int applyNumber)
{
    if (playerIndex <= 0 ||
        playerIndex > 3 ||
        !golfer)
    {
        return false;
    }

    PlayerConfig& config =
        g_config[playerIndex];

    auto* g =
        reinterpret_cast<uint8_t*>(
            golfer);

    __try
    {
        uint32_t expectedFace =
            ToZeroBased(config.face);

        uint32_t faceBefore =
            ReadField(
                g,
                0x054);

        char diagnostic[320] = {};

        sprintf_s(
            diagnostic,
            "[REAPPLY #%d] P%d golfer=0x%08X "
            "face-before=%u expected=%u%s\r\n",
            applyNumber,
            playerIndex + 1,
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    golfer)),
            faceBefore,
            expectedFace,
            faceBefore == expectedFace
                ? ""
                : " *** CHANGED BY GAME ***");

        AppendLog(diagnostic);

        uint32_t genderValue =
            config.female ? 1u : 0u;

        WriteField(
            g,
            0x390,
            genderValue);

        WriteField(
            g,
            0x30DC,
            genderValue);

        if (config.overrideOutfit)
        {
            WriteField(
                g,
                0x054,
                ToZeroBased(config.face));

            WriteField(
                g,
                0x0B0,
                ToZeroBased(config.shirt));

            WriteField(
                g,
                0x10C,
                ToZeroBased(config.bottoms));

            WriteField(
                g,
                0x168,
                ToZeroBased(config.bodysuit));

            WriteField(
                g,
                0x1C4,
                ToZeroBased(config.shoes));

            WriteField(
                g,
                0x220,
                static_cast<uint32_t>(
                    config.hat));
        }

        // Clubs/Balls are direct UI/INI values.
        WriteField(
            g,
            0x3EC,
            static_cast<uint32_t>(
                config.clubs));

        WriteField(
            g,
            0x448,
            static_cast<uint32_t>(
                config.balls));

        char buffer[640] = {};

        sprintf_s(
            buffer,
            "[APPLY #%d] P%d golfer=0x%08X "
            "gender=%s(%u) override=%d "
            "native(face=%u shirt=%u bottoms=%u "
            "bodysuit=%u shoes=%u hat=%u "
            "clubs=%u balls=%u)\r\n",
            applyNumber,
            playerIndex + 1,
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    golfer)),
            config.female ? "Female" : "Male",
            genderValue,
            config.overrideOutfit ? 1 : 0,
            ToZeroBased(config.face),
            ToZeroBased(config.shirt),
            ToZeroBased(config.bottoms),
            ToZeroBased(config.bodysuit),
            ToZeroBased(config.shoes),
            static_cast<unsigned>(
                config.hat),
            static_cast<unsigned>(
                config.clubs),
            static_cast<unsigned>(
                config.balls));

        AppendLog(buffer);

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        char buffer[256] = {};

        sprintf_s(
            buffer,
            "[APPLY ERROR #%d] P%d golfer=0x%08X\r\n",
            applyNumber,
            playerIndex + 1,
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    golfer)));

        AppendLog(buffer);

        return false;
    }
}

static void AddCandidate(
    void* golfer,
    void* caller)
{
    if (!golfer)
        return;

    LONG sequence =
        InterlockedIncrement(
            &g_candidateSequence) - 1;

    int index =
        static_cast<int>(
            static_cast<unsigned long>(
                sequence) %
            MAX_CANDIDATES);

    GolferCandidate& candidate =
        g_candidates[index];

    InterlockedExchange(
        &candidate.valid,
        0);

    candidate.golfer =
        golfer;

    candidate.caller =
        caller;

    candidate.resolvedPlayer =
        -1;

    candidate.applyCount =
        0;

    candidate.nextApplyTick =
        0;

    MemoryBarrier();

    InterlockedExchange(
        &candidate.valid,
        1);

    char buffer[192] = {};

    sprintf_s(
        buffer,
        "[CANDIDATE #%ld] golfer=0x%08X caller=0x%08X slot=%d\r\n",
        sequence + 1,
        static_cast<unsigned>(
            reinterpret_cast<uintptr_t>(
                golfer)),
        static_cast<unsigned>(
            reinterpret_cast<uintptr_t>(
                caller)),
        index);

    AppendLog(buffer);
}

extern "C" void __cdecl ObserveGolfer(
    void* golfer,
    void* caller)
{
    AddCandidate(
        golfer,
        caller);
}

static bool IsCustomizeMenuCandidate(
    const GolferCandidate& candidate)
{
    const uintptr_t caller =
        reinterpret_cast<uintptr_t>(candidate.caller);

    return caller == 0x0834992A ||
           caller == 0x08349962 ||
           caller == 0x08349970;
}

static void ProcessCandidate(
    GolferCandidate& candidate)
{
    if (InterlockedCompareExchange(
            &candidate.valid,
            1,
            1) != 1)
    {
        return;
    }

    if (!candidate.golfer)
        return;

    // Golden Tee creates temporary golfer objects for Options -> Customize
    // through these callers. They all use the same low slot value (0xD60),
    // so the normal gameplay resolver incorrectly identifies every preview
    // golfer as P3. The game has already populated these temporary objects
    // with the active player's correct appearance; leave them untouched.
    if (IsCustomizeMenuCandidate(candidate))
    {
        if (candidate.resolvedPlayer != -2)
        {
            char skipped[256] = {};
            sprintf_s(
                skipped,
                "[CUSTOMIZE SKIP] golfer=0x%08X caller=0x%08X\r\n",
                static_cast<unsigned>(reinterpret_cast<uintptr_t>(candidate.golfer)),
                static_cast<unsigned>(reinterpret_cast<uintptr_t>(candidate.caller)));
            AppendLog(skipped);
            candidate.resolvedPlayer = -2;
        }

        return;
    }

    /*
        Once we resolve ownership, keep that player assignment for
        this constructor lifetime. We deliberately do NOT repeatedly
        walk the owner chain after resolution.
    */
    if (candidate.resolvedPlayer < 0)
    {
        uint32_t slot = 0;

        int player =
            GetPlayerIndex(
                candidate.golfer,
                &slot);

        if (player < 0 ||
            player > 3)
        {
            return;
        }

        candidate.resolvedPlayer =
            player;

        char found[256] = {};

        sprintf_s(
            found,
            "[FOUND] P%d golfer=0x%08X "
            "slot=0x%08X low=0x%03X\r\n",
            player + 1,
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    candidate.golfer)),
            slot,
            slot & 0xFFF);

        AppendLog(found);

        if (player == 0)
        {
            // P1 remains entirely controlled by existing TeknoParrot logic.
            candidate.applyCount =
                APPLY_COUNT;

            return;
        }

        candidate.nextApplyTick =
            0;
    }

    int player =
        candidate.resolvedPlayer;

    if (player <= 0 ||
        player > 3)
    {
        return;
    }

    if (candidate.applyCount >= APPLY_COUNT)
        return;

    ULONGLONG now =
        GetTickCount64();

    if (candidate.applyCount > 0 &&
        now < candidate.nextApplyTick)
    {
        return;
    }

    int applyNumber =
        candidate.applyCount + 1;

    if (ApplyPlayerConfig(
            player,
            candidate.golfer,
            applyNumber))
    {
        candidate.applyCount++;

        candidate.nextApplyTick =
            GetTickCount64() +
            APPLY_INTERVAL_MS;

        if (candidate.applyCount ==
            APPLY_COUNT)
        {
            char done[256] = {};

            sprintf_s(
                done,
                "[REAPPLY COMPLETE] P%d golfer=0x%08X "
                "%d full applies completed.\r\n",
                player + 1,
                static_cast<unsigned>(
                    reinterpret_cast<uintptr_t>(
                        candidate.golfer)),
                APPLY_COUNT);

            AppendLog(done);
        }
    }
}

static DWORD WINAPI PlayerResolverThread(
    LPVOID)
{
    AppendLog(
        "[RESOLVER] 1 ms resolver; single verification apply.\r\n");

    while (InterlockedCompareExchange(
               &g_running,
               1,
               1))
    {
        for (int i = 0;
             i < MAX_CANDIDATES;
             i++)
        {
            ProcessCandidate(
                g_candidates[i]);
        }

        Sleep(1);
    }

    return 0;
}

extern "C" void __cdecl ApplyStartupPlayer(
    int playerIndex,
    void* container)
{
    if (playerIndex <= 0 ||
        playerIndex > 3 ||
        !container)
    {
        return;
    }

    __try
    {
        auto* c =
            reinterpret_cast<uint8_t*>(
                container);

        void* golfer =
            *reinterpret_cast<void**>(
                c + 0x08);

        if (!golfer)
            return;

        char buffer[256] = {};

        sprintf_s(
            buffer,
            "[SYNC STARTUP] P%d container=0x%08X golfer=0x%08X\r\n",
            playerIndex + 1,
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    container)),
            static_cast<unsigned>(
                reinterpret_cast<uintptr_t>(
                    golfer)));

        AppendLog(buffer);

        ApplyPlayerConfig(
            playerIndex,
            golfer,
            0);

        QueuePlayerInitials(
            playerIndex);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        AppendLog(
            "[SYNC STARTUP ERROR]\r\n");
    }
}

extern "C" __declspec(naked)
void GTStartupPlayerHook()
{
    __asm
    {
        pushfd
        pushad

        push ebp
        push ebx

        call ApplyStartupPlayer
        add esp, 8

        popad
        popfd

        // Original 6 bytes at 0x08254CA3:
        // c7 06 00 00 00 00
        mov dword ptr [esi], 0

        jmp STARTUP_PLAYER_RETURN
    }
}

static bool InstallStartupPlayerHook()
{
    auto* address =
        reinterpret_cast<uint8_t*>(
            STARTUP_PLAYER_HOOK);

    const uint8_t expected[6] =
    {
        0xC7, 0x06, 0x00,
        0x00, 0x00, 0x00
    };

    if (memcmp(
            address,
            expected,
            sizeof(expected)) != 0)
    {
        char buffer[256] = {};

        sprintf_s(
            buffer,
            "[SYNC INSTALL ERROR] Unexpected bytes at "
            "0x08254CA3: %02X %02X %02X %02X %02X %02X\r\n",
            address[0],
            address[1],
            address[2],
            address[3],
            address[4],
            address[5]);

        AppendLog(buffer);

        return false;
    }

    uintptr_t hook =
        reinterpret_cast<uintptr_t>(
            &GTStartupPlayerHook);

    intptr_t delta =
        static_cast<intptr_t>(hook) -
        static_cast<intptr_t>(
            STARTUP_PLAYER_HOOK + 5);

    if (delta < INT32_MIN ||
        delta > INT32_MAX)
    {
        AppendLog(
            "[SYNC INSTALL ERROR] Hook outside rel32 range.\r\n");

        return false;
    }

    DWORD oldProtection = 0;

    if (!VirtualProtect(
            address,
            6,
            PAGE_EXECUTE_READWRITE,
            &oldProtection))
    {
        AppendLog(
            "[SYNC INSTALL ERROR] VirtualProtect failed.\r\n");

        return false;
    }

    address[0] = 0xE9;

    int32_t relative =
        static_cast<int32_t>(
            delta);

    memcpy(
        address + 1,
        &relative,
        sizeof(relative));

    address[5] = 0x90;

    FlushInstructionCache(
        GetCurrentProcess(),
        address,
        6);

    DWORD ignored = 0;

    VirtualProtect(
        address,
        6,
        oldProtection,
        &ignored);

    AppendLog(
        "[SYNC INSTALL] Startup player hook installed at 0x08254CA3.\r\n");

    return true;
}

extern "C" __declspec(naked)
void GTGolferHook()
{
    __asm
    {
        mov eax, [esp]
        mov ecx, [esp + 4]

        pushfd
        pushad

        push eax
        push ecx

        call ObserveGolfer
        add esp, 8

        popad
        popfd

        jmp dword ptr [g_existingHookTarget]
    }
}

static bool InstallHook()
{
    constexpr uintptr_t GT_FUNCTION =
        0x083445BE;

    auto* address =
        reinterpret_cast<uint8_t*>(
            GT_FUNCTION);

    if (address[0] != 0xE9)
    {
        char buffer[128] = {};

        sprintf_s(
            buffer,
            "[ERROR] Expected E9 at 0x083445BE; found %02X\r\n",
            address[0]);

        AppendLog(buffer);

        return false;
    }

    int32_t oldRelative =
        *reinterpret_cast<int32_t*>(
            address + 1);

    g_existingHookTarget =
        GT_FUNCTION +
        5 +
        oldRelative;

    uintptr_t ourHook =
        reinterpret_cast<uintptr_t>(
            &GTGolferHook);

    intptr_t delta =
        static_cast<intptr_t>(
            ourHook) -
        static_cast<intptr_t>(
            GT_FUNCTION + 5);

    if (delta < INT32_MIN ||
        delta > INT32_MAX)
    {
        AppendLog(
            "[ERROR] Our hook is outside rel32 range.\r\n");

        return false;
    }

    char startup[256] = {};

    sprintf_s(
        startup,
        "[INSTALL] Existing hook=0x%08X OurHook=0x%08X\r\n",
        static_cast<unsigned>(
            g_existingHookTarget),
        static_cast<unsigned>(
            ourHook));

    AppendLog(startup);

    DWORD oldProtection = 0;

    if (!VirtualProtect(
            address,
            5,
            PAGE_EXECUTE_READWRITE,
            &oldProtection))
    {
        AppendLog(
            "[ERROR] VirtualProtect failed.\r\n");

        return false;
    }

    int32_t newRelative =
        static_cast<int32_t>(
            delta);

    address[0] = 0xE9;

    memcpy(
        address + 1,
        &newRelative,
        sizeof(newRelative));

    FlushInstructionCache(
        GetCurrentProcess(),
        address,
        5);

    DWORD ignored = 0;

    VirtualProtect(
        address,
        5,
        oldProtection,
        &ignored);

    AppendLog(
        "[INSTALL] Hook installed successfully.\r\n");

    return true;
}

static DWORD WINAPI InitializeHelper(
    LPVOID module)
{
    DeleteFileA(
        "C:\\TeknoParrot\\GTPlayerDefaults-hook.txt");

    char buffer[256] = {};

    sprintf_s(
        buffer,
        "[LOAD] GoldenTeePlayerDefaults "
        "module=0x%08X PID=%lu\r\n",
        static_cast<unsigned>(
            reinterpret_cast<uintptr_t>(
                module)),
        GetCurrentProcessId());

    AppendLog(buffer);

    if (!LoadConfiguration())
    {
        AppendLog(
            "[ERROR] Configuration initialization failed; "
            "hook not installed.\r\n");

        return 1;
    }

    Sleep(250);

    if (!InstallHook())
        return 1;

    if (!InstallStartupPlayerHook())
        return 1;

    HANDLE resolver =
        CreateThread(
            nullptr,
            0,
            PlayerResolverThread,
            nullptr,
            0,
            nullptr);

    if (resolver)
        CloseHandle(resolver);

    HANDLE initialsWriter =
        CreateThread(
            nullptr,
            0,
            InitialsWriterThread,
            nullptr,
            0,
            nullptr);

    if (initialsWriter)
        CloseHandle(initialsWriter);

    return 0;
}

BOOL APIENTRY DllMain(
    HMODULE module,
    DWORD reason,
    LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(
            module);

        HANDLE thread =
            CreateThread(
                nullptr,
                0,
                InitializeHelper,
                module,
                0,
                nullptr);

        if (thread)
            CloseHandle(thread);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        InterlockedExchange(
            &g_running,
            0);
    }

    return TRUE;
}
