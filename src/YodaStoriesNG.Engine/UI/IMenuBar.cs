using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// What the engine needs from a menu bar, whichever one it got.
///
/// Windows uses <see cref="NativeMenuBar"/>, a real OS menu drawn outside the client area
/// at the right DPI. Everywhere else that is user32.dll and therefore not an option, so
/// <see cref="MenuBar"/> draws the same menus with SDL inside a strip the window reserves
/// at the top. Construction differs - one wants an HWND, the other a renderer - so the
/// engine picks and sets up the implementation itself and talks to it through this after.
/// </summary>
public unsafe interface IMenuBar : IDisposable
{
    event Action<WorldSize>? OnNewGame;
    event Action? OnSaveGame;
    event Action? OnSaveGameAs;
    event Action? OnLoadGame;
    event Action? OnExit;
    event Action? OnAssetViewer;
    event Action? OnScriptEditor;
    event Action? OnMapViewer;
    event Action? OnSaveInspector;
    event Action? OnZoneEditor;
    event Action? OnEnableBot;
    event Action? OnDisableBot;
    event Action<int>? OnSetScale;
    event Action? OnToggleSmoothMovement;
    bool SmoothMovement { get; set; }
    event Action? OnShowKeyboardControls;
    event Action? OnShowControllerControls;
    event Action? OnSelectDataFile;
    event Action? OnShowAbout;
    event Action? OnShowHighScores;
    event Action? OnHideMenuBar;

    /// <summary>
    /// Whether the bar is showing. Controller players can put it away and use the pause
    /// menu instead; F10 or the pause menu brings it back.
    /// </summary>
    bool Visible { get; set; }

    /// <summary>True while a dropdown is open, so the game can ignore input underneath it.</summary>
    bool IsMenuOpen { get; }

    /// <summary>Returns true if the event was consumed and should go no further.</summary>
    bool HandleEvent(SDLEvent* evt);

    void Render();

    /// <summary>Closes any open dropdown.</summary>
    void Close();
}
