using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// The menu for someone holding a controller. The menu bar wants a mouse or a keyboard,
/// so Start (or Back) opens this panel over the paused game instead: the D-pad or left
/// stick moves, A picks, B or Start again closes. Arrows, Enter and Esc work as well, so it
/// never traps a player who put the pad down.
///
/// The engine hands it the items; labels are functions so toggles can show their state.
/// </summary>
public unsafe class ControllerOverlay
{
    public sealed record Item(Func<string> Label, Action Run, bool KeepOpen = false);

    private const int ItemHeight = 26;
    private const int Width = 300;
    private const int StickPress = 16000;
    private const int StickRelease = 8000;

    private readonly BitmapFont _font;
    private readonly Item[] _items;
    private int _selected;
    private bool _stickHeld;

    public bool IsOpen { get; private set; }

    /// <summary>Raised on open and close, so the engine can pause the game underneath.</summary>
    public event Action<bool>? OnOpenChanged;

    public ControllerOverlay(BitmapFont font, Item[] items)
    {
        _font = font;
        _items = items;
    }

    public void Open()
    {
        IsOpen = true;
        _selected = 0;
        OnOpenChanged?.Invoke(true);
    }

    public void Close()
    {
        IsOpen = false;
        OnOpenChanged?.Invoke(false);
    }

    /// <summary>Returns true if the event was consumed and should go no further.</summary>
    public bool HandleEvent(SDLEvent* evt)
    {
        var type = (SDLEventType)evt->Type;

        if (!IsOpen)
        {
            if (type == SDLEventType.Controllerbuttondown &&
                evt->Cbutton.Button is (byte)SDLGameControllerButton.Start or (byte)SDLGameControllerButton.Back)
            {
                Open();
                return true;
            }
            return false;
        }

        switch (type)
        {
            case SDLEventType.Controllerbuttondown:
                switch ((SDLGameControllerButton)evt->Cbutton.Button)
                {
                    case SDLGameControllerButton.DpadUp: Move(-1); break;
                    case SDLGameControllerButton.DpadDown: Move(1); break;
                    case SDLGameControllerButton.A: Pick(); break;
                    case SDLGameControllerButton.B:
                    case SDLGameControllerButton.Start:
                    case SDLGameControllerButton.Back: Close(); break;
                }
                return true;

            case SDLEventType.Controlleraxismotion when evt->Caxis.Axis == (byte)SDLGameControllerAxis.Lefty:
                // One step per push, not one per frame: fire past StickPress, re-arm under StickRelease.
                int value = evt->Caxis.Value;
                if (!_stickHeld && Math.Abs(value) > StickPress)
                {
                    _stickHeld = true;
                    Move(value < 0 ? -1 : 1);
                }
                else if (Math.Abs(value) < StickRelease)
                {
                    _stickHeld = false;
                }
                return true;

            case SDLEventType.Keydown:
                switch (evt->Key.Keysym.Sym)
                {
                    case (int)SDLKeyCode.Up: Move(-1); break;
                    case (int)SDLKeyCode.Down: Move(1); break;
                    case (int)SDLKeyCode.Return:
                    case (int)SDLKeyCode.KpEnter: Pick(); break;
                    case (int)SDLKeyCode.Escape: Close(); break;
                }
                return true;

            case SDLEventType.Controllerbuttonup:
            case SDLEventType.Controlleraxismotion:
            case SDLEventType.Keyup:
                return true;
        }

        return false;
    }

    private void Move(int step) => _selected = (_selected + step + _items.Length) % _items.Length;

    private void Pick()
    {
        var item = _items[_selected];
        if (!item.KeepOpen)
            Close();
        item.Run();
    }

    /// <summary>Draws centred in the game area; call while the game-area viewport is active.</summary>
    public void Render(SDLRenderer* renderer)
    {
        if (!IsOpen)
            return;

        SDL.SetRenderDrawBlendMode(renderer, SDLBlendMode.Blend);
        SDL.SetRenderDrawColor(renderer, 0, 0, 0, 160);
        var dim = new SDLRect { X = 0, Y = 0, W = GameRenderer.GameAreaWidth, H = GameRenderer.WindowHeight };
        SDL.RenderFillRect(renderer, &dim);

        int height = (_items.Length + 2) * ItemHeight + 10;
        int left = (GameRenderer.GameAreaWidth - Width) / 2;
        int top = (GameRenderer.WindowHeight - height) / 2;

        SDL.SetRenderDrawColor(renderer, 50, 53, 60, 255);
        var panel = new SDLRect { X = left, Y = top, W = Width, H = height };
        SDL.RenderFillRect(renderer, &panel);
        SDL.SetRenderDrawColor(renderer, 70, 75, 85, 255);
        SDL.RenderDrawRect(renderer, &panel);

        const string title = "PAUSED";
        _font.RenderText(renderer, title, left + (Width - _font.GetTextWidth(title)) / 2, top + 8, 1, 255, 220, 120, 255);

        for (int i = 0; i < _items.Length; i++)
        {
            int y = top + (i + 1) * ItemHeight + 4;
            bool selected = i == _selected;

            if (selected)
            {
                SDL.SetRenderDrawColor(renderer, 70, 100, 140, 255);
                var highlight = new SDLRect { X = left + 4, Y = y, W = Width - 8, H = ItemHeight - 4 };
                SDL.RenderFillRect(renderer, &highlight);
            }

            byte shade = selected ? (byte)255 : (byte)200;
            _font.RenderText(renderer, _items[i].Label(), left + 16, y + 6, 1, shade, shade, shade, 255);
        }

        const string hint = "A select   B close";
        _font.RenderText(renderer, hint, left + (Width - _font.GetTextWidth(hint)) / 2,
            top + height - ItemHeight + 6, 1, 150, 150, 150, 255);
    }
}
