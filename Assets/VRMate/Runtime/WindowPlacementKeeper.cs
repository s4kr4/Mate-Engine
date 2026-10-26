using System.Collections.Generic;
using Kirurobo;
using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Brings the window back to the size state and the position it had when the app last ran, and saves
    /// the position into settings.json whenever it settles. Upstream saves the size state but restores it
    /// only from AvatarSettingsMenu, which the main scene does not contain, and never saves the position.
    /// WindowPlacement decides where the window goes and PlacementSaver when to save; this component reads
    /// and writes UniWindowController and SaveLoadHandler. Positions and monitor rectangles both come from
    /// UniWindowController, so they share its coordinates; values from the WinAPI (top-left origin) are not
    /// mixed in.
    /// </summary>
    [DisallowMultipleComponent]
    public class WindowPlacementKeeper : MonoBehaviour
    {
        // How long the window may keep a size other than its size state before that is reported once.
        private const float SizeWarningSeconds = 5f;

        // The window size may differ from the requested one by rounding to whole pixels.
        private const float SizeTolerance = 1f;

        private static WindowPlacementKeeper instance;

        private readonly PlacementSaver saver = new PlacementSaver();
        private readonly WarnOnce warnings = new WarnOnce();
        private readonly List<ScreenRect> monitors = new List<ScreenRect>();

        private Phase phase = Phase.WaitingForWindow;
        private UniWindowController controller;
        private float trackingSince;
        private bool sizeEverMatched;

        private enum Phase
        {
            WaitingForWindow,
            Restoring,
            Tracking,
        }

        private void Awake()
        {
            // A second copy would restore the window again and save the same position twice.
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
        }

        private void Update()
        {
            var handler = SaveLoadHandler.Instance;
            if (handler == null || handler.data == null || !FindController())
            {
                return;
            }

            var settings = handler.data;
            switch (phase)
            {
                case Phase.WaitingForWindow:
                    // Before the native window exists, windowSize reads as zero and writes go nowhere.
                    var size = controller.windowSize;
                    if (!(size.x > 0f && size.y > 0f))
                    {
                        return;
                    }

                    controller.windowSize = SizeOf(settings.windowSizeState);
                    phase = Phase.Restoring;
                    return;

                case Phase.Restoring:
                    // A frame after the resize, so the window has the size the saved position was seen at.
                    Restore(settings);
                    saver.Begin(controller.windowPosition.x, controller.windowPosition.y);
                    trackingSince = Time.unscaledTime;
                    phase = Phase.Tracking;
                    return;

                default:
                    var position = controller.windowPosition;
                    if (saver.Tick(Time.unscaledTime, position.x, position.y, SizeMatches(settings)))
                    {
                        Save(handler, position);
                    }

                    return;
            }
        }

        private void OnApplicationQuit()
        {
            var handler = SaveLoadHandler.Instance;
            if (phase != Phase.Tracking || handler == null || handler.data == null || controller == null)
            {
                return;
            }

            var position = controller.windowPosition;
            if (saver.ShouldSaveOnQuit(position.x, position.y, SizeMatches(handler.data)))
            {
                Save(handler, position);
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        private bool FindController()
        {
            if (controller == null)
            {
                // current searches the scene while none is cached; once found it is kept here.
                controller = UniWindowController.current;
            }

            return controller != null;
        }

        private void Restore(SaveLoadHandler.SettingsData settings)
        {
            if (!settings.hasWindowPosition)
            {
                return;
            }

            monitors.Clear();
            var count = UniWindowController.GetMonitorCount();
            for (var i = 0; i < count; i++)
            {
                // A monitor that cannot be read comes back as Rect.zero, which WindowPlacement ignores.
                var rect = UniWindowController.GetMonitorRect(i);
                monitors.Add(new ScreenRect(rect.x, rect.y, rect.width, rect.height));
            }

            var size = SizeOf(settings.windowSizeState);
            float x;
            float y;
            if (WindowPlacement.TryRestore(settings.windowPositionX, settings.windowPositionY, size.x, size.y, monitors, out x, out y))
            {
                controller.windowPosition = new Vector2(x, y);
            }
        }

        // Also false while the window is stretched over a whole monitor (AvatarBigScreenHandler), so the
        // position it has then is not saved.
        private bool SizeMatches(SaveLoadHandler.SettingsData settings)
        {
            var expected = SizeOf(settings.windowSizeState);
            var actual = controller.windowSize;
            var matches = Mathf.Abs(actual.x - expected.x) <= SizeTolerance && Mathf.Abs(actual.y - expected.y) <= SizeTolerance;
            if (matches)
            {
                sizeEverMatched = true;
            }
            else if (!sizeEverMatched
                && Time.unscaledTime - trackingSince >= SizeWarningSeconds
                && warnings.ShouldWarn("size"))
            {
                Debug.LogWarning(
                    "[VRMate] the window size " + actual.x + "x" + actual.y + " has not matched the size state "
                    + settings.windowSizeState + " (" + expected.x + "x" + expected.y + "); the window position is not saved");
            }

            return matches;
        }

        private static void Save(SaveLoadHandler handler, Vector2 position)
        {
            handler.data.hasWindowPosition = true;
            handler.data.windowPositionX = position.x;
            handler.data.windowPositionY = position.y;
            handler.SaveToDisk();
        }

        // The same sizes as SettingsHandlerButtons.CycleWindowSize; a value outside the enum is Normal.
        private static Vector2 SizeOf(SaveLoadHandler.SettingsData.WindowSizeState state)
        {
            switch (state)
            {
                case SaveLoadHandler.SettingsData.WindowSizeState.Big:
                    return new Vector2(2048f, 1536f);
                case SaveLoadHandler.SettingsData.WindowSizeState.Small:
                    return new Vector2(768f, 512f);
                default:
                    return new Vector2(1536f, 1024f);
            }
        }
    }
}
