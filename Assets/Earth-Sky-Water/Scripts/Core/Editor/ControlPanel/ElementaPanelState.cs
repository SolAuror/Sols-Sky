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
        public Vector2 contentScroll;

        /// <summary>
        /// Sections the author has explicitly opened or closed. Absence means "use the
        /// page's default", so adding a section later does not have to guess what an
        /// existing window meant by a missing entry.
        /// </summary>
        public List<string> openedSections = new();
        public List<string> closedSections = new();

        [Header("Authorities")]
        public TimeOfDay timeOfDay;
        public SolWeatherManager weatherManager;

        [Header("Weather authoring")]
        public SolWeatherProfileAsset weatherA;
        public SolWeatherProfileAsset weatherB;
        public float weatherBlend;
        public bool previewWeather = true;
        public int inspectedWeatherIndex = -1;
        public bool instantWeatherChange = true;

        [Header("Wind")]
        public float windDegrees;
        public bool windInitialized;
        public bool showSceneWind = true;

        [Header("Property filters")]
        public string skyFilter = string.Empty;
        public string weatherFilter = string.Empty;
        public string cloudFilter = string.Empty;
        public string waterFilter = string.Empty;
        public string waterQualityFilter = string.Empty;
        public string landscapeFilter = string.Empty;

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
    }
}
