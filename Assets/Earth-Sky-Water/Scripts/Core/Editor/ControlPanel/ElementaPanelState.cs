using System;
using System.Collections.Generic;
using Sol.ToD;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Everything the control panel has to remember across a domain reload: which page and
    /// sections the author left open, which assets they were comparing, and the preview they
    /// had running. Kept in one serialized object rather than as fields on the window so a
    /// page can persist state without the shell growing a field for each one.
    /// </summary>
    [Serializable]
    sealed class ElementaPanelState
    {
        [Header("Shell")]
        public int page;
        public string pageId;
        public bool skyNight;
        public bool cloudsWeather = true;
        public SolWeatherProfileAsset inspectedWeather;
        public Sol.Water.SolWaterBody selectedWaterBody;
        public int landscapeLayer;
        public Sol.Landscape.SolLandscapeGroup landscapeGroup;
        public string landscapeActivity = "Materials";
        public int landscapeTool;
        public float landscapeBrushSize = 20;
        public float landscapeBrushStrength = .2f;
        public float landscapeBrushHardness = .5f;
        public float landscapeBrushRotation;
        public UnityEngine.Object landscapeBrush;
        public bool landscapeProbe = true;

        /// <summary>
        /// One scroll position per page. A single shared one carried the scroll across a
        /// page switch, so leaving a long page half-way down dropped you into the middle of
        /// the next one.
        /// </summary>
        public List<Vector2> pageScroll = new();

        /// <summary>
        /// Sections the author has explicitly opened or closed. Absence means "use the
        /// page's default", so adding a section later does not have to guess what an
        /// existing window meant by a missing entry.
        /// </summary>
        public List<string> openedSections = new();
        public List<string> closedSections = new();

        /// <summary>
        /// Per-editor field filters, keyed the same way sections are. Parallel lists rather
        /// than a field per editor, because every component the panel exposes needs one and
        /// the set grows whenever a manager is added.
        /// </summary>
        public List<string> filterKeys = new();
        public List<string> filterValues = new();

        [Header("Authorities")]
        public TimeOfDay timeOfDay;
        public SolWeatherManager weatherManager;

        [Header("Weather authoring")]
        public SolWeatherProfileAsset weatherA;
        public SolWeatherProfileAsset weatherB;
        public float weatherBlend;
        public bool previewWeather;
        public int inspectedWeatherIndex = -1;
        public bool instantWeatherChange = true;

        [Header("Wind")]
        public float windDegrees;
        public bool windInitialized;
        public bool showSceneWind = true;

        public bool IsSectionOpen(string key, bool defaultOpen)
        {
            if (openedSections.Contains(key))
                return true;
            if (closedSections.Contains(key))
                return false;
            return defaultOpen;
        }

        public void SetSectionOpen(string key, bool open)
        {
            openedSections.Remove(key);
            closedSections.Remove(key);
            (open ? openedSections : closedSections).Add(key);
        }

        public Vector2 GetScroll(int page)
            => page >= 0 && page < pageScroll.Count ? pageScroll[page] : Vector2.zero;

        public void SetScroll(int page, Vector2 value)
        {
            if (page < 0)
                return;
            while (pageScroll.Count <= page)
                pageScroll.Add(Vector2.zero);
            pageScroll[page] = value;
        }

        public string GetFilter(string key)
        {
            int index = filterKeys.IndexOf(key);
            return index >= 0 ? filterValues[index] : string.Empty;
        }

        public void SetFilter(string key, string value)
        {
            int index = filterKeys.IndexOf(key);
            if (index >= 0)
            {
                filterValues[index] = value;
                return;
            }

            filterKeys.Add(key);
            filterValues.Add(value);
        }
    }
}
