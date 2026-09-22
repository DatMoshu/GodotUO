/*
 * A native test plugin: the smallest DLL that speaks ClassicUO's plugin ABI.
 *
 * It exports Install(PluginHeader*), which is what Plugin.Load looks for
 * first, registers for packets both ways, position changes and the lifecycle
 * callbacks, and asks the client where the player is. Everything it sees goes
 * to a log that tools/plugin_probe/run.py reads back. It changes nothing: every
 * packet is passed on untouched.
 *
 * The header layout is the one in Plugin.cs (upstream and port alike):
 * cuoapi's PluginHeader followed by the fields added since. x64 only, like GUO.
 */

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>

typedef struct PluginHeader
{
    int32_t ClientVersion;
    void *HWND;
    void *OnRecv;
    void *OnSend;
    void *OnHotkeyPressed;
    void *OnMouse;
    void *OnPlayerPositionChanged;
    void *OnClientClosing;
    void *OnInitialize;
    void *OnConnected;
    void *OnDisconnected;
    void *OnFocusGained;
    void *OnFocusLost;
    void *GetUOFilePath;
    void *Recv;
    void *Send;
    void *GetPacketLength;
    void *GetPlayerPosition;
    void *CastSpell;
    void *GetStaticImage;
    void *Tick;
    void *RequestMove;
    void *SetTitle;
    void *OnRecv_new;
    void *OnSend_new;
    void *Recv_new;
    void *Send_new;
    void *OnDrawCmdList;
    void *SDL_Window;
    void *OnWndProc;
    void *GetStaticData;
    void *GetTileData;
    void *GetCliloc;
} PluginHeader;

/* What the client hands the plugin to call. Its OnGetPlayerPosition has no
 * MarshalAs on the return, so that bool is a 4-byte BOOL; the OnRecv_new and
 * OnSend_new the plugin fills in are I1, one byte. */
typedef int (*get_player_position_fn)(int *x, int *y, int *z); /* default bool: 4-byte BOOL */
typedef short (*get_packet_length_fn)(int id);
typedef const char *(*get_uo_file_path_fn)(void);

static FILE *g_log;
static get_player_position_fn g_get_player_position;
static get_packet_length_fn g_get_packet_length;
static long g_recv, g_send, g_positions, g_ticks;

static void logf_(const char *fmt, ...)
{
    va_list ap;

    if (!g_log)
        return;

    va_start(ap, fmt);
    vfprintf(g_log, fmt, ap);
    va_end(ap);
    fputc('\n', g_log);
    fflush(g_log);
}

static void open_log(void)
{
    char path[MAX_PATH];
    char dir[MAX_PATH];
    DWORD n = GetEnvironmentVariableA("GUO_PLUGIN_PROBE_LOG_DIR", dir, sizeof(dir));

    if (n > 0 && n < sizeof(dir))
        snprintf(path, sizeof(path), "%s\\native.log", dir);
    else
        snprintf(path, sizeof(path), "guo_probe_native.log");

    g_log = fopen(path, "w");
}

static void __cdecl on_initialize(void)
{
    logf_("initialize");
}

static void __cdecl on_connected(void)
{
    logf_("connected");
}

static void __cdecl on_disconnected(void)
{
    logf_("disconnected");
}

static void __cdecl on_closing(void)
{
    logf_("totals recv=%ld send=%ld positions=%ld ticks=%ld", g_recv, g_send, g_positions, g_ticks);
    logf_("closing");
}

static void __cdecl on_tick(void)
{
    g_ticks++;
}

static uint8_t __cdecl on_recv(uint8_t *data, int *length)
{
    if (++g_recv <= 10)
        logf_("recv id=0x%02X len=%d table_len=%d", data[0], *length,
              g_get_packet_length ? g_get_packet_length(data[0]) : -2);

    return 1;
}

static uint8_t __cdecl on_send(uint8_t *data, int *length)
{
    if (++g_send <= 10)
        logf_("send id=0x%02X len=%d", data[0], *length);

    return 1;
}

static void __cdecl on_player_position(int x, int y, int z)
{
    g_positions++;

    if (g_positions <= 20)
    {
        int px = 0, py = 0, pz = 0;
        int ok = g_get_player_position ? g_get_player_position(&px, &py, &pz) : -1;

        logf_("position x=%d y=%d z=%d get_player_position ok=%d x=%d y=%d z=%d recv=%ld send=%ld",
              x, y, z, ok, px, py, pz, g_recv, g_send);
    }
}

__declspec(dllexport) void __cdecl Install(PluginHeader *header)
{
    open_log();

    g_get_player_position = (get_player_position_fn)header->GetPlayerPosition;
    g_get_packet_length = (get_packet_length_fn)header->GetPacketLength;

    logf_("install client_version=0x%08X hwnd=%p sdl_window=%p is_window=%d uo_path=%s",
          header->ClientVersion, header->HWND, header->SDL_Window,
          header->HWND ? IsWindow((HWND)header->HWND) : 0,
          header->GetUOFilePath ? ((get_uo_file_path_fn)header->GetUOFilePath)() : "(none)");

    header->OnInitialize = (void *)on_initialize;
    header->OnConnected = (void *)on_connected;
    header->OnDisconnected = (void *)on_disconnected;
    header->OnClientClosing = (void *)on_closing;
    header->Tick = (void *)on_tick;
    header->OnRecv_new = (void *)on_recv;
    header->OnSend_new = (void *)on_send;
    header->OnPlayerPositionChanged = (void *)on_player_position;
}
