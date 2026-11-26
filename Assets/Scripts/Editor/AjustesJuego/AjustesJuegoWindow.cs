using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Planetitas.EditorTools
{
    /// <summary>Ventana para ajustar el salto, la gravedad y las estrellas de energía.</summary>
    public class AjustesJuegoWindow : EditorWindow
    {
        const string Folder = "Assets/Scripts/Editor/AjustesJuego";
        const string AssetFolder = "Assets/Resources";
        const string AssetPath = AssetFolder + "/" + JumpGameSettings.ResourcePath + ".asset";

        JumpGameSettings settings;
        SerializedObject serialized;
        Label jumpInfo, footerHint;
        HelpBox startWarning, maxWarning;

        [MenuItem("Tools/Planetitas/Ajustes del juego")]
        public static void Open()
        {
            var window = GetWindow<AjustesJuegoWindow>();
            window.titleContent = new GUIContent("Ajustes Planetitas");
            window.minSize = new Vector2(360f, 420f);
        }

        public void CreateGUI()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Folder + "/AjustesJuegoWindow.uxml");
            if (tree == null)
            {
                rootVisualElement.Add(new HelpBox("No se encontró " + Folder + "/AjustesJuegoWindow.uxml.", HelpBoxMessageType.Error));
                return;
            }
            tree.CloneTree(rootVisualElement);

            settings = LoadOrCreateSettings();
            serialized = new SerializedObject(settings);

            var root = rootVisualElement;
            jumpInfo = root.Q<Label>("jumpInfo");
            footerHint = root.Q<Label>("footerHint");
            startWarning = root.Q<HelpBox>("startWarning");
            maxWarning = root.Q<HelpBox>("maxWarning");
            root.Q<Button>("resetButton").clicked += ResetToDefaults;

            // Bind conecta cada campo con su binding-path y da Ctrl+Z gratis.
            root.Bind(serialized);
            root.TrackSerializedObjectValue(serialized, _ => OnSettingsChanged());
            Refresh();
        }

        static JumpGameSettings LoadOrCreateSettings()
        {
            var existing = AssetDatabase.LoadAssetAtPath<JumpGameSettings>(AssetPath);
            if (existing != null) return existing;

            if (!AssetDatabase.IsValidFolder(AssetFolder))
                AssetDatabase.CreateFolder("Assets", "Resources");
            var created = CreateInstance<JumpGameSettings>();
            AssetDatabase.CreateAsset(created, AssetPath);
            AssetDatabase.SaveAssets();
            Debug.Log("Planetitas: creado " + AssetPath + " con los valores por defecto.");
            return created;
        }

        void OnSettingsChanged()
        {
            // la distancia máxima nunca puede ser menor que la inicial
            if (settings.maxOrbSpacing < settings.startOrbSpacing)
            {
                Undo.RecordObject(settings, "Ajustar distancia máxima");
                settings.maxOrbSpacing = settings.startOrbSpacing;
                EditorUtility.SetDirty(settings);
                serialized.Update();
            }
            Refresh();
        }

        void Refresh()
        {
            float first = settings.FirstJumpHeight;
            float max = settings.MaxJumpHeight;
            jumpInfo.text = $"Altura del primer salto: {first:0.0} m   ·   con impulso máximo: {max:0.0} m";

            SetWarning(startWarning, settings.startOrbSpacing > first,
                $"La distancia inicial ({settings.startOrbSpacing:0.0} m) es mayor que el primer salto ({first:0.0} m): al empezar necesitarás varios saltos por estrella.");
            SetWarning(maxWarning, settings.maxOrbSpacing > max,
                $"La distancia máxima ({settings.maxOrbSpacing:0.0} m) supera lo que sube un salto con impulso máximo ({max:0.0} m): arriba será muy difícil no quedarse sin energía.");

            footerHint.text = EditorApplication.isPlaying
                ? "En Play: los cambios se aplican al instante y se conservan al parar."
                : "Los cambios se guardan en " + AssetPath + ".";
        }

        static void SetWarning(HelpBox box, bool show, string message)
        {
            box.text = message;
            box.EnableInClassList("hidden", !show);
        }

        void ResetToDefaults()
        {
            Undo.RecordObject(settings, "Restaurar ajustes de Planetitas");
            settings.ResetToDefaults();
            EditorUtility.SetDirty(settings);
            serialized.Update();
            Refresh();
        }

        void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Save();
        }

        void OnLostFocus() => Save();

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            Save();
            if (jumpInfo != null) Refresh();
        }

        void Save()
        {
            if (settings != null) AssetDatabase.SaveAssetIfDirty(settings);
        }
    }
}
