using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ComfyUIBridge.Editor
{
    // =========================================================
    // ENUMS
    // =========================================================

    public enum ComfyNodeType
    {
        TextPrompt,
        KSampler,
        EmptyLatentImage,
        EmptyLatentAudio,
        CheckpointLoader,
        LoraLoader,
        ImageInput,
        GenericInput
    }

    public enum ComfyOutputType
    {
        Image,
        Audio,
        Object3D,
        All
    }

    // =========================================================
    // NODE CONFIGURATION
    // =========================================================

    [Serializable]
    public class ComfyNodeConfig
    {
        public ComfyNodeType type = ComfyNodeType.TextPrompt;
        public string nodeID = "";
        public string displayName = "";

        // TextPrompt
        [TextArea(2, 5)] public string textValue = "";

        // KSampler
        public bool randomizeSeed = true;
        public long manualSeed = 12345;
        public int steps = 20;
        public float cfg = 8f;
        public float denoise = 1f;
        public bool overrideSampler = false;
        public string samplerName = "euler";
        public bool overrideScheduler = false;
        public string scheduler = "normal";

        // EmptyLatentImage
        public int width = 1024;
        public int height = 1024;
        public int batchSize = 1;

        // EmptyLatentAudio
        public float audioSeconds = 5f;

        // CheckpointLoader
        public string checkpointName = "";

        // LoraLoader
        public string loraName = "";
        public float strengthModel = 1f;
        public float strengthClip = 1f;

        // ImageInput
        public Texture2D inputImage;
        public string inputImagePath = "";

        // GenericInput
        public string genericKey = "";
        public string genericValue = "";

        // Runtime (not serialized)
        [NonSerialized] public string uploadedServerFilename;

        // -----------------------------------------------------
        // Apply this config's values into the workflow dictionary
        // -----------------------------------------------------
        public void ApplyToWorkflow(Dictionary<string, object> workflow)
        {
            if (string.IsNullOrEmpty(nodeID))
                return;

            if (!workflow.ContainsKey(nodeID))
            {
                Debug.LogWarning("[ComfyUI Bridge] Node not found in workflow: " + nodeID);
                return;
            }

            Dictionary<string, object> node = workflow[nodeID] as Dictionary<string, object>;
            if (node == null) return;

            Dictionary<string, object> inputs = node.ContainsKey("inputs") ? node["inputs"] as Dictionary<string, object> : null;
            if (inputs == null)
            {
                Debug.LogWarning("[ComfyUI Bridge] Node has no inputs: " + nodeID);
                return;
            }

            string classType = node.ContainsKey("class_type")
                ? Convert.ToString(node["class_type"], CultureInfo.InvariantCulture)
                : "";
            ValidateClassType(classType);

            switch (type)
            {
                case ComfyNodeType.TextPrompt:
                {
                    bool any = false;
                    if (SetInput(inputs, "text", textValue, false)) any = true;
                    if (SetInput(inputs, "text_g", textValue, false)) any = true;
                    if (SetInput(inputs, "text_l", textValue, false)) any = true;
                    if (SetInput(inputs, "string", textValue, false)) any = true;
                    if (!any)
                        Debug.LogWarning("[ComfyUI Bridge] No text input found on node " + nodeID);
                    break;
                }

                case ComfyNodeType.KSampler:
                {
                    long seedToUse = randomizeSeed ? CreateRandomSeed() : manualSeed;
                    manualSeed = seedToUse;

                    if (!SetInput(inputs, "seed", seedToUse, false))
                        SetInput(inputs, "noise_seed", seedToUse, false);

                    SetInput(inputs, "steps", Mathf.Max(1, steps), false);
                    SetInput(inputs, "cfg", cfg, false);
                    SetInput(inputs, "denoise", denoise, false);

                    if (overrideSampler)
                        SetInput(inputs, "sampler_name", samplerName, false);
                    if (overrideScheduler)
                        SetInput(inputs, "scheduler", scheduler, false);
                    break;
                }

                case ComfyNodeType.EmptyLatentImage:
                    SetInput(inputs, "width", Mathf.Max(64, width), false);
                    SetInput(inputs, "height", Mathf.Max(64, height), false);
                    SetInput(inputs, "batch_size", Mathf.Max(1, batchSize), false);
                    break;

                case ComfyNodeType.EmptyLatentAudio:
                    SetInput(inputs, "seconds", audioSeconds, false);
                    break;

                case ComfyNodeType.CheckpointLoader:
                {
                    if (string.IsNullOrEmpty(checkpointName)) break;
                    bool any = false;
                    if (SetInput(inputs, "ckpt_name", checkpointName, false)) any = true;
                    else if (SetInput(inputs, "vae_name", checkpointName, false)) any = true;
                    else if (SetInput(inputs, "model_name", checkpointName, false)) any = true;
                    else if (SetInput(inputs, "unet_name", checkpointName, false)) any = true;
                    if (!any)
                        Debug.LogWarning("[ComfyUI Bridge] No checkpoint/model input found on node " + nodeID);
                    break;
                }

                case ComfyNodeType.LoraLoader:
                    if (!string.IsNullOrEmpty(loraName))
                        SetInput(inputs, "lora_name", loraName, false);
                    SetInput(inputs, "strength_model", strengthModel, false);
                    SetInput(inputs, "strength_clip", strengthClip, false);
                    break;

                case ComfyNodeType.ImageInput:
                    if (!string.IsNullOrEmpty(uploadedServerFilename))
                        SetInput(inputs, "image", uploadedServerFilename, false);
                    else
                        Debug.LogWarning("[ComfyUI Bridge] Image not uploaded for node " + nodeID);
                    break;

                case ComfyNodeType.GenericInput:
                    if (!string.IsNullOrEmpty(genericKey))
                        SetInput(inputs, genericKey, genericValue, false);
                    break;
            }
        }

        void ValidateClassType(string classType)
        {
            if (string.IsNullOrEmpty(classType)) return;
            string lower = classType.ToLowerInvariant();
            bool mismatch = false;

            switch (type)
            {
                case ComfyNodeType.KSampler:
                    if (!lower.Contains("sampler")) mismatch = true;
                    break;
                case ComfyNodeType.TextPrompt:
                    if (!lower.Contains("text") && !lower.Contains("string") && !lower.Contains("prompt"))
                        mismatch = true;
                    break;
                case ComfyNodeType.EmptyLatentImage:
                    if (!lower.Contains("empty") || !lower.Contains("image")) mismatch = true;
                    break;
                case ComfyNodeType.EmptyLatentAudio:
                    if (!lower.Contains("empty") || !lower.Contains("audio")) mismatch = true;
                    break;
                case ComfyNodeType.CheckpointLoader:
                    if (!lower.Contains("checkpoint") && !lower.Contains("vae") &&
                        !lower.Contains("unet") && !lower.Contains("model") && !lower.Contains("loader"))
                        mismatch = true;
                    break;
                case ComfyNodeType.LoraLoader:
                    if (!lower.Contains("lora")) mismatch = true;
                    break;
                case ComfyNodeType.ImageInput:
                    if (!lower.Contains("image") && !lower.Contains("load")) mismatch = true;
                    break;
            }

            if (mismatch)
                Debug.LogWarning("[ComfyUI Bridge] Type mismatch on node " + nodeID +
                                 ": JSON is '" + classType + "', configured as '" + type + "'.");
        }

        static bool SetInput(Dictionary<string, object> inputs, string key, object value, bool warnIfMissing = true)
        {
            if (inputs.ContainsKey(key))
            {
                inputs[key] = value;
                return true;
            }
            if (warnIfMissing)
                Debug.LogWarning("[ComfyUI Bridge] Input '" + key + "' not found.");
            return false;
        }

        static long CreateRandomSeed()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            long value = BitConverter.ToInt64(bytes, 0);
            return Math.Abs(value % 9000000000000000L) + 1L;
        }
    }

    // =========================================================
    // OUTPUT / PRESET DATA
    // =========================================================

    class ComfyOutput
    {
        public string filename;
        public string subfolder;
        public string type;
        public string extension;
    }

    [Serializable]
    class ComfyPreset
    {
        public string serverUrl = "http://127.0.0.1:8188";
        public string workflowPath = "";
        public string outputFolder = "Assets/ComfyUIBridge/Generated";
        public int expectedOutput = 0;
        public List<ComfyNodeConfig> nodes = new List<ComfyNodeConfig>();
    }

    // =========================================================
    // MAIN EDITOR WINDOW
    // =========================================================

    public class ComfyUIBridgeEditorWindow : EditorWindow
    {
        const string DefaultWorkflowPath = "Assets/ComfyUIBridge/Workflows/text2image.json";
        const string DefaultGeneratedFolder = "Assets/ComfyUIBridge/Generated";
        const string PresetFolder = "Assets/ComfyUIBridge/Presets";

        const string PrefsServerUrl = "ComfyUIBridge.ServerUrl";
        const string PrefsOutputFolder = "ComfyUIBridge.OutputFolder";
        const string PrefsWorkflowPath = "ComfyUIBridge.WorkflowPath";
        const string PrefsOutputType = "ComfyUIBridge.OutputType";

        static readonly string[] CommonSamplers =
        {
            "euler", "euler_ancestral", "euler_ancestral_pp",
            "dpmpp_2m", "dpmpp_2m_sde", "dpmpp_sde",
            "dpm_fast", "dpm_adaptive",
            "ddim", "uni_pc", "uni_pc_bh2",
            "lms", "heun"
        };

        static readonly string[] CommonSchedulers =
        {
            "normal", "karras", "exponential",
            "simple", "ddim_uniform", "sgm_uniform"
        };

        // --- Settings ---
        string serverUrl = "http://127.0.0.1:8188";
        TextAsset workflowAsset;
        string workflowPath = DefaultWorkflowPath;
        string outputFolder = DefaultGeneratedFolder;
        ComfyOutputType expectedOutput = ComfyOutputType.Image;
        List<ComfyNodeConfig> nodeConfigs = new List<ComfyNodeConfig>();

        // --- State ---
        bool isGenerating;
        string status = "Ready.";
        string lastPromptId;
        List<Texture2D> previewTextures = new List<Texture2D>();
        List<string> outputAssetPaths = new List<string>();
        Vector2 scroll;

        [MenuItem("Tools/ComfyUI Bridge/Generator")]
        public static void Open()
        {
            GetWindow<ComfyUIBridgeEditorWindow>("ComfyUI Bridge");
        }

        void OnEnable()
        {
            serverUrl = EditorPrefs.GetString(PrefsServerUrl, "http://127.0.0.1:8188");
            outputFolder = EditorPrefs.GetString(PrefsOutputFolder, DefaultGeneratedFolder);
            workflowPath = EditorPrefs.GetString(PrefsWorkflowPath, DefaultWorkflowPath);
            expectedOutput = (ComfyOutputType)EditorPrefs.GetInt(PrefsOutputType, 0);

            workflowAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(workflowPath);

            if (nodeConfigs == null)
                nodeConfigs = new List<ComfyNodeConfig>();
            if (previewTextures == null)
                previewTextures = new List<Texture2D>();
            if (outputAssetPaths == null)
                outputAssetPaths = new List<string>();
        }

        void OnDisable()
        {
            EditorPrefs.SetString(PrefsServerUrl, serverUrl);
            EditorPrefs.SetString(PrefsOutputFolder, outputFolder);
            EditorPrefs.SetString(PrefsWorkflowPath, workflowPath);
            EditorPrefs.SetInt(PrefsOutputType, (int)expectedOutput);
        }

        // =========================================================
        // MAIN UI
        // =========================================================

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawConnectionSection();
            DrawWorkflowSection();
            DrawOutputSection();
            DrawNodeListSection();
            DrawGenerateSection();
            DrawPreviewSection();

            EditorGUILayout.EndScrollView();
        }

        void DrawConnectionSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            serverUrl = EditorGUILayout.TextField("Server URL", serverUrl);
        }

        void DrawWorkflowSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Workflow", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            workflowAsset = (TextAsset)EditorGUILayout.ObjectField(
                "Workflow JSON", workflowAsset, typeof(TextAsset), false);
            if (EditorGUI.EndChangeCheck() && workflowAsset != null)
            {
                workflowPath = AssetDatabase.GetAssetPath(workflowAsset);
            }

            workflowPath = EditorGUILayout.TextField("Workflow Path", workflowPath);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use Default Workflow"))
                {
                    workflowPath = DefaultWorkflowPath;
                    workflowAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(workflowPath);
                }

                if (GUILayout.Button("Open Output Folder"))
                {
                    EnsureFolder(outputFolder);
                    EditorUtility.RevealInFinder(outputFolder);
                }
            }
        }

        void DrawOutputSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output Settings", EditorStyles.boldLabel);
            expectedOutput = (ComfyOutputType)EditorGUILayout.EnumPopup("Expected Output", expectedOutput);
            outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        }

        // =========================================================
        // NODE LIST UI
        // =========================================================

        void DrawNodeListSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Node Configuration", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Auto Discover", GUILayout.Width(100)))
                {
                    AutoDiscoverNodes();
                }

                if (GUILayout.Button("Add Node", GUILayout.Width(80)))
                {
                    ShowAddNodeMenu();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Save Preset", GUILayout.Width(80)))
                {
                    SavePresetDialog();
                }

                if (GUILayout.Button("Load Preset", GUILayout.Width(80)))
                {
                    LoadPresetDialog();
                }
            }

            EditorGUILayout.HelpBox(
                "Add nodes to match your workflow JSON. Set the Node ID to the key in the JSON (e.g. \"6\"). " +
                "Use \"Auto Discover\" to scan the workflow and auto-create configs.",
                MessageType.None);

            if (nodeConfigs.Count == 0)
            {
                EditorGUILayout.LabelField("  (no nodes configured — click \"Auto Discover\" or \"Add Node\")",
                    EditorStyles.miniLabel);
            }

            // Draw each node config
            List<int> toRemove = new List<int>();
            for (int i = 0; i < nodeConfigs.Count; i++)
            {
                bool remove = DrawNodeConfig(nodeConfigs[i], i);
                if (remove) toRemove.Add(i);
            }
            for (int i = toRemove.Count - 1; i >= 0; i--)
                nodeConfigs.RemoveAt(toRemove[i]);
        }

        bool DrawNodeConfig(ComfyNodeConfig config, int index)
        {
            bool requestRemove = false;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.Space(1);

            // --- Header row ---
            using (new EditorGUILayout.HorizontalScope())
            {
                config.type = (ComfyNodeType)EditorGUILayout.EnumPopup(
                    config.type, GUILayout.Width(140));

                EditorGUILayout.LabelField("ID", GUILayout.Width(22));
                config.nodeID = EditorGUILayout.TextField(config.nodeID, GUILayout.Width(50));

                config.displayName = EditorGUILayout.TextField(
                    string.IsNullOrEmpty(config.displayName) ? " " : config.displayName);

                if (GUILayout.Button("Load", GUILayout.Width(46)))
                {
                    LoadDefaultsFromJSON(config);
                }

                if (GUILayout.Button("\u00d7", GUILayout.Width(22)))
                {
                    requestRemove = true;
                }
            }

            // --- Type-specific fields ---
            EditorGUI.indentLevel++;

            switch (config.type)
            {
                case ComfyNodeType.TextPrompt:
                    EditorGUILayout.LabelField("Text", EditorStyles.miniLabel);
                    config.textValue = EditorGUILayout.TextArea(config.textValue, GUILayout.MinHeight(48));
                    break;

                case ComfyNodeType.KSampler:
                    config.randomizeSeed = EditorGUILayout.Toggle("Random Seed", config.randomizeSeed);
                    if (!config.randomizeSeed)
                        config.manualSeed = EditorGUILayout.LongField("Seed", config.manualSeed);
                    config.steps = EditorGUILayout.IntSlider("Steps", config.steps, 1, 150);
                    config.cfg = EditorGUILayout.Slider("CFG", config.cfg, 0f, 30f);
                    config.denoise = EditorGUILayout.Slider("Denoise", config.denoise, 0f, 1f);

                    config.overrideSampler = EditorGUILayout.Toggle("Override Sampler", config.overrideSampler);
                    if (config.overrideSampler)
                    {
                        int idx = Mathf.Max(0, Array.IndexOf(CommonSamplers, config.samplerName));
                        idx = EditorGUILayout.Popup("Sampler Name", idx, CommonSamplers);
                        config.samplerName = CommonSamplers[idx];
                    }

                    config.overrideScheduler = EditorGUILayout.Toggle("Override Scheduler", config.overrideScheduler);
                    if (config.overrideScheduler)
                    {
                        int idx = Mathf.Max(0, Array.IndexOf(CommonSchedulers, config.scheduler));
                        idx = EditorGUILayout.Popup("Scheduler", idx, CommonSchedulers);
                        config.scheduler = CommonSchedulers[idx];
                    }
                    break;

                case ComfyNodeType.EmptyLatentImage:
                    config.width = EditorGUILayout.IntField("Width", config.width);
                    config.height = EditorGUILayout.IntField("Height", config.height);
                    config.batchSize = EditorGUILayout.IntField("Batch Size", config.batchSize);
                    break;

                case ComfyNodeType.EmptyLatentAudio:
                    config.audioSeconds = EditorGUILayout.Slider("Seconds", config.audioSeconds, 0.5f, 60f);
                    break;

                case ComfyNodeType.CheckpointLoader:
                    config.checkpointName = EditorGUILayout.TextField("Checkpoint / Model Name", config.checkpointName);
                    break;

                case ComfyNodeType.LoraLoader:
                    config.loraName = EditorGUILayout.TextField("LoRA Name", config.loraName);
                    config.strengthModel = EditorGUILayout.Slider("Strength Model", config.strengthModel, -2f, 2f);
                    config.strengthClip = EditorGUILayout.Slider("Strength Clip", config.strengthClip, -2f, 2f);
                    break;

                case ComfyNodeType.ImageInput:
                    EditorGUI.BeginChangeCheck();
                    config.inputImage = (Texture2D)EditorGUILayout.ObjectField(
                        "Image", config.inputImage, typeof(Texture2D), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        config.inputImagePath = config.inputImage != null
                            ? AssetDatabase.GetAssetPath(config.inputImage)
                            : "";
                    }
                    if (config.inputImage != null)
                    {
                        float aspect = config.inputImage.height > 0
                            ? (float)config.inputImage.width / config.inputImage.height
                            : 1f;
                        float pw = Mathf.Min(200f, position.width - 60f);
                        Rect previewRect = GUILayoutUtility.GetRect(pw, pw / Mathf.Max(0.01f, aspect));
                        GUI.DrawTexture(previewRect, config.inputImage, ScaleMode.ScaleToFit);
                    }
                    break;

                case ComfyNodeType.GenericInput:
                    config.genericKey = EditorGUILayout.TextField("Input Key", config.genericKey);
                    config.genericValue = EditorGUILayout.TextField("Value", config.genericValue);
                    break;
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(1);
            EditorGUILayout.EndVertical();

            return requestRemove;
        }

        void ShowAddNodeMenu()
        {
            GenericMenu menu = new GenericMenu();
            string[] names = Enum.GetNames(typeof(ComfyNodeType));
            foreach (string name in names)
            {
                ComfyNodeType captured = (ComfyNodeType)Enum.Parse(typeof(ComfyNodeType), name);
                menu.AddItem(new GUIContent(NodeTypeDisplayName(captured)), false, () =>
                {
                    nodeConfigs.Add(new ComfyNodeConfig { type = captured, nodeID = "" });
                });
            }
            menu.ShowAsContext();
        }

        static string NodeTypeDisplayName(ComfyNodeType type)
        {
            switch (type)
            {
                case ComfyNodeType.TextPrompt: return "Text Prompt";
                case ComfyNodeType.KSampler: return "KSampler";
                case ComfyNodeType.EmptyLatentImage: return "Empty Latent Image";
                case ComfyNodeType.EmptyLatentAudio: return "Empty Latent Audio";
                case ComfyNodeType.CheckpointLoader: return "Checkpoint / VAE Loader";
                case ComfyNodeType.LoraLoader: return "LoRA Loader";
                case ComfyNodeType.ImageInput: return "Image Input (Upload)";
                case ComfyNodeType.GenericInput: return "Generic Input";
                default: return type.ToString();
            }
        }

        // =========================================================
        // GENERATE BUTTON + STATUS
        // =========================================================

        void DrawGenerateSection()
        {
            EditorGUILayout.Space(10);

            using (new EditorGUI.DisabledScope(isGenerating))
            {
                if (GUILayout.Button("Generate", GUILayout.Height(32)))
                {
                    Generate();
                }
            }

            if (isGenerating && GUILayout.Button("Mark Idle"))
            {
                isGenerating = false;
                status = "Stopped waiting in Unity. ComfyUI may still be processing the queued prompt.";
            }

            EditorGUILayout.HelpBox(status, MessageType.Info);

            if (!string.IsNullOrEmpty(lastPromptId))
            {
                EditorGUILayout.SelectableLabel("Prompt ID: " + lastPromptId,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        void DrawPreviewSection()
        {
            if (previewTextures == null || previewTextures.Count == 0)
            {
                if (outputAssetPaths != null && outputAssetPaths.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Output Files (" + outputAssetPaths.Count + ")", EditorStyles.boldLabel);
                    foreach (string path in outputAssetPaths)
                        EditorGUILayout.SelectableLabel(path, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Results (" + previewTextures.Count + ")", EditorStyles.boldLabel);

            foreach (Texture2D tex in previewTextures)
            {
                if (tex == null) continue;
                float maxWidth = Mathf.Max(220f, position.width - 28f);
                float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
                Rect rect = GUILayoutUtility.GetRect(maxWidth, maxWidth / Mathf.Max(0.01f, aspect),
                    GUILayout.ExpandWidth(true));
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
            }
        }

        // =========================================================
        // GENERATION PIPELINE
        // =========================================================

        async void Generate()
        {
            if (isGenerating) return;

            isGenerating = true;
            lastPromptId = null;
            previewTextures.Clear();
            outputAssetPaths.Clear();
            status = "Preparing workflow...";
            Repaint();

            try
            {
                // 1. Read workflow JSON
                string fullPath = ToFullPath(workflowPath);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("Workflow JSON was not found.", fullPath);

                string workflowJson = File.ReadAllText(fullPath, Encoding.UTF8);
                Dictionary<string, object> workflow = MiniJson.Deserialize(workflowJson) as Dictionary<string, object>;
                if (workflow == null)
                    throw new InvalidDataException("Workflow JSON root must be an object.");

                // 2. Upload images for ImageInput nodes
                int uploadIndex = 0;
                foreach (ComfyNodeConfig config in nodeConfigs)
                {
                    if (config.type != ComfyNodeType.ImageInput || config.inputImage == null)
                        continue;

                    uploadIndex++;
                    status = "Uploading image " + uploadIndex + " (node " + config.nodeID + ")...";
                    Repaint();

                    Texture2D readable = GetReadableTexture2D(config.inputImage);
                    byte[] pngBytes = readable.EncodeToPNG();
                    if (readable != config.inputImage)
                        DestroyImmediate(readable);

                    string tempName = "unity_" + Guid.NewGuid().ToString("N") + ".png";
                    string uploadUrl = NormalizeServerUrl(serverUrl) + "/upload/image";

                    string uploadResponse = await UploadImageMultipart(uploadUrl, pngBytes, tempName);
                    Dictionary<string, object> resp = MiniJson.Deserialize(uploadResponse) as Dictionary<string, object>;

                    if (resp == null || !resp.ContainsKey("name"))
                        throw new Exception("Image upload failed for node " + config.nodeID + ": " + uploadResponse);

                    config.uploadedServerFilename = Convert.ToString(resp["name"], CultureInfo.InvariantCulture);
                }

                // 3. Apply all node configs
                foreach (ComfyNodeConfig config in nodeConfigs)
                    config.ApplyToWorkflow(workflow);

                // 4. Queue prompt
                status = "Queueing prompt...";
                Repaint();

                string clientId = Guid.NewGuid().ToString("N");
                Dictionary<string, object> request = new Dictionary<string, object>
                {
                    { "client_id", clientId },
                    { "prompt", workflow }
                };

                string promptResponse = await PostJson(
                    NormalizeServerUrl(serverUrl) + "/prompt", MiniJson.Serialize(request));
                Dictionary<string, object> promptData = MiniJson.Deserialize(promptResponse) as Dictionary<string, object>;

                if (promptData == null || !promptData.ContainsKey("prompt_id"))
                    throw new InvalidDataException("ComfyUI did not return a prompt_id: " + promptResponse);

                lastPromptId = Convert.ToString(promptData["prompt_id"], CultureInfo.InvariantCulture);

                // 5. Wait for outputs
                status = "Waiting for ComfyUI...";
                Repaint();

                List<ComfyOutput> outputs = await WaitForOutputs(lastPromptId);

                // 6. Download and import
                EnsureFolder(outputFolder);

                for (int i = 0; i < outputs.Count; i++)
                {
                    ComfyOutput output = outputs[i];
                    status = "Downloading " + (i + 1) + "/" + outputs.Count + ": " + output.filename + "...";
                    Repaint();

                    string assetPath = await DownloadAndImportOutput(output);
                    if (!string.IsNullOrEmpty(assetPath))
                        outputAssetPaths.Add(assetPath);
                }

                // 7. Select first result
                if (previewTextures.Count > 0)
                {
                    Selection.activeObject = previewTextures[0];
                }
                else if (outputAssetPaths.Count > 0)
                {
                    UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outputAssetPaths[0]);
                    if (obj != null) Selection.activeObject = obj;
                }

                status = "Done. " + outputs.Count + " file(s) saved to " + outputFolder;
            }
            catch (Exception ex)
            {
                status = ex.Message;
                Debug.LogException(ex);
            }
            finally
            {
                isGenerating = false;
                Repaint();
            }
        }

        // =========================================================
        // IMAGE UPLOAD
        // =========================================================

        static Task<string> UploadImageMultipart(string url, byte[] imageBytes, string filename)
        {
            return Task.Run(() =>
            {
                string boundary = "----UnityComfyUI" + Guid.NewGuid().ToString("N");
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "multipart/form-data; boundary=" + boundary;
                request.Timeout = 60000;

                string crlf = "\r\n";

                using (MemoryStream ms = new MemoryStream())
                {
                    // overwrite field
                    byte[] part1 = Encoding.UTF8.GetBytes(
                        "--" + boundary + crlf +
                        "Content-Disposition: form-data; name=\"overwrite\"" + crlf + crlf +
                        "true" + crlf);
                    ms.Write(part1, 0, part1.Length);

                    // image file
                    byte[] part2 = Encoding.UTF8.GetBytes(
                        "--" + boundary + crlf +
                        "Content-Disposition: form-data; name=\"image\"; filename=\"" + filename + "\"" + crlf +
                        "Content-Type: image/png" + crlf + crlf);
                    ms.Write(part2, 0, part2.Length);
                    ms.Write(imageBytes, 0, imageBytes.Length);

                    // closing boundary
                    byte[] part3 = Encoding.UTF8.GetBytes(crlf + "--" + boundary + "--" + crlf);
                    ms.Write(part3, 0, part3.Length);

                    request.ContentLength = ms.Length;
                    using (Stream rs = request.GetRequestStream())
                        ms.WriteTo(rs);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            });
        }

        static Texture2D GetReadableTexture2D(Texture2D source)
        {
            if (source == null) return null;
            if (source.isReadable) return source;

            RenderTexture rt = RenderTexture.GetTemporary(
                source.width, source.height, 0,
                RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        // =========================================================
        // OUTPUT WAITING + COLLECTION
        // =========================================================

        async Task<List<ComfyOutput>> WaitForOutputs(string promptId)
        {
            string historyUrl = NormalizeServerUrl(serverUrl) + "/history/" + Uri.EscapeDataString(promptId);
            DateTime timeout = DateTime.UtcNow.AddMinutes(10);

            while (DateTime.UtcNow < timeout)
            {
                await Task.Delay(1000);
                string historyJson = await GetText(historyUrl);
                Dictionary<string, object> history = MiniJson.Deserialize(historyJson) as Dictionary<string, object>;

                if (history != null && history.ContainsKey(promptId))
                {
                    List<ComfyOutput> all = FindAllOutputs(history, promptId);
                    if (all.Count > 0)
                    {
                        // Filter by expected type
                        List<ComfyOutput> filtered = new List<ComfyOutput>();
                        foreach (ComfyOutput o in all)
                        {
                            if (IsExpectedOutput(o, expectedOutput))
                                filtered.Add(o);
                        }
                        return filtered.Count > 0 ? filtered : all;
                    }
                }

                status = "Waiting for ComfyUI... " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                Repaint();
            }

            throw new TimeoutException("Timed out waiting for ComfyUI history output.");
        }

        static List<ComfyOutput> FindAllOutputs(Dictionary<string, object> history, string promptId)
        {
            List<ComfyOutput> results = new List<ComfyOutput>();

            if (history == null || !history.ContainsKey(promptId))
                return results;

            Dictionary<string, object> item = history[promptId] as Dictionary<string, object>;
            Dictionary<string, object> outputs = item != null && item.ContainsKey("outputs")
                ? item["outputs"] as Dictionary<string, object>
                : null;
            if (outputs == null) return results;

            foreach (object nodeValue in outputs.Values)
            {
                Dictionary<string, object> node = nodeValue as Dictionary<string, object>;
                if (node == null) continue;

                foreach (object value in node.Values)
                {
                    List<object> list = value as List<object>;
                    if (list == null) continue;

                    foreach (object fileObj in list)
                    {
                        Dictionary<string, object> file = fileObj as Dictionary<string, object>;
                        if (file == null || !file.ContainsKey("filename")) continue;

                        string filename = Convert.ToString(file["filename"], CultureInfo.InvariantCulture);
                        results.Add(new ComfyOutput
                        {
                            filename = filename,
                            subfolder = file.ContainsKey("subfolder")
                                ? Convert.ToString(file["subfolder"], CultureInfo.InvariantCulture)
                                : "",
                            type = file.ContainsKey("type")
                                ? Convert.ToString(file["type"], CultureInfo.InvariantCulture)
                                : "output",
                            extension = Path.GetExtension(filename).ToLowerInvariant()
                        });
                    }
                }
            }

            return results;
        }

        static bool IsExpectedOutput(ComfyOutput output, ComfyOutputType expected)
        {
            string ext = output.extension;
            switch (expected)
            {
                case ComfyOutputType.Image:
                    return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";
                case ComfyOutputType.Audio:
                    return ext == ".wav" || ext == ".mp3" || ext == ".ogg" ||
                           ext == ".aif" || ext == ".aiff" || ext == ".flac";
                case ComfyOutputType.Object3D:
                    return ext == ".glb" || ext == ".gltf" || ext == ".obj" || ext == ".fbx";
                case ComfyOutputType.All:
                    return true;
                default:
                    return false;
            }
        }

        // =========================================================
        // DOWNLOAD + IMPORT
        // =========================================================

        async Task<string> DownloadAndImportOutput(ComfyOutput output)
        {
            string viewUrl = BuildViewUrl(output);
            byte[] data = await DownloadBytes(viewUrl);

            string safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(output.filename));
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string assetPath = outputFolder + "/" + timestamp + "_" + safeName + output.extension;

            File.WriteAllBytes(ToFullPath(assetPath), data);
            AssetDatabase.ImportAsset(assetPath);

            // Type-specific loading
            switch (output.extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".webp":
                {
                    Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (tex != null)
                        previewTextures.Add(tex);
                    break;
                }
                case ".wav":
                case ".mp3":
                case ".ogg":
                {
                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                    if (clip != null)
                        Debug.Log("[ComfyUI Bridge] Audio imported: " + assetPath + " (" + clip.length + "s)");
                    break;
                }
                default:
                    Debug.Log("[ComfyUI Bridge] File saved: " + assetPath);
                    break;
            }

            return assetPath;
        }

        string BuildViewUrl(ComfyOutput output)
        {
            return NormalizeServerUrl(serverUrl)
                + "/view?filename=" + Uri.EscapeDataString(output.filename)
                + "&subfolder=" + Uri.EscapeDataString(output.subfolder ?? "")
                + "&type=" + Uri.EscapeDataString(string.IsNullOrEmpty(output.type) ? "output" : output.type);
        }

        // =========================================================
        // AUTO DISCOVER + LOAD DEFAULTS
        // =========================================================

        void AutoDiscoverNodes()
        {
            string fullPath = ToFullPath(workflowPath);
            if (!File.Exists(fullPath))
            {
                status = "Workflow file not found: " + fullPath;
                return;
            }

            string json = File.ReadAllText(fullPath, Encoding.UTF8);
            Dictionary<string, object> workflow = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (workflow == null)
            {
                status = "Invalid workflow JSON.";
                return;
            }

            nodeConfigs.Clear();

            foreach (KeyValuePair<string, object> kvp in workflow)
            {
                Dictionary<string, object> node = kvp.Value as Dictionary<string, object>;
                if (node == null) continue;

                string classType = node.ContainsKey("class_type")
                    ? Convert.ToString(node["class_type"], CultureInfo.InvariantCulture)
                    : "";
                ComfyNodeType? inferred = InferNodeType(classType);
                if (!inferred.HasValue) continue;

                ComfyNodeConfig config = new ComfyNodeConfig
                {
                    type = inferred.Value,
                    nodeID = kvp.Key,
                    displayName = classType
                };
                LoadDefaultsFromJSON(config, workflow);
                nodeConfigs.Add(config);
            }

            status = "Discovered " + nodeConfigs.Count + " configurable node(s).";
            Repaint();
        }

        static ComfyNodeType? InferNodeType(string classType)
        {
            if (string.IsNullOrEmpty(classType)) return null;
            string lower = classType.ToLowerInvariant();

            if (lower.Contains("ksampler") || lower == "samplercustom")
                return ComfyNodeType.KSampler;
            if (lower.Contains("cliptextencode") || lower.Contains("textencode") ||
                lower.Contains("prompt"))
                return ComfyNodeType.TextPrompt;
            if (lower.Contains("emptylatentimage") ||
                (lower.Contains("empty") && lower.Contains("latent") && lower.Contains("image")))
                return ComfyNodeType.EmptyLatentImage;
            if (lower.Contains("empty") && lower.Contains("audio"))
                return ComfyNodeType.EmptyLatentAudio;
            if (lower.Contains("checkpoint") || lower == "vaeloader" ||
                lower == "unetloader" || lower == "unetloadgguf")
                return ComfyNodeType.CheckpointLoader;
            if (lower.Contains("lora"))
                return ComfyNodeType.LoraLoader;
            if (lower == "loadimage" || lower.Contains("loadimage"))
                return ComfyNodeType.ImageInput;

            return null;
        }

        void LoadDefaultsFromJSON(ComfyNodeConfig config)
        {
            string fullPath = ToFullPath(workflowPath);
            if (!File.Exists(fullPath))
            {
                status = "Workflow file not found.";
                return;
            }

            string json = File.ReadAllText(fullPath, Encoding.UTF8);
            Dictionary<string, object> workflow = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (workflow == null)
            {
                status = "Invalid workflow JSON.";
                return;
            }

            LoadDefaultsFromJSON(config, workflow);
            status = "Loaded defaults for node " + config.nodeID;
            Repaint();
        }

        static void LoadDefaultsFromJSON(ComfyNodeConfig config, Dictionary<string, object> workflow)
        {
            if (string.IsNullOrEmpty(config.nodeID) || !workflow.ContainsKey(config.nodeID))
                return;

            Dictionary<string, object> node = workflow[config.nodeID] as Dictionary<string, object>;
            Dictionary<string, object> inputs = node != null && node.ContainsKey("inputs")
                ? node["inputs"] as Dictionary<string, object>
                : null;
            if (inputs == null) return;

            switch (config.type)
            {
                case ComfyNodeType.TextPrompt:
                {
                    string txt = GetInputString(inputs, "text")
                        ?? GetInputString(inputs, "text_g")
                        ?? GetInputString(inputs, "text_l")
                        ?? GetInputString(inputs, "string");
                    if (txt != null) config.textValue = txt;
                    break;
                }

                case ComfyNodeType.KSampler:
                {
                    long? s = GetInputLong(inputs, "seed") ?? GetInputLong(inputs, "noise_seed");
                    if (s.HasValue) { config.manualSeed = s.Value; config.randomizeSeed = false; }
                    int? st = GetInputInt(inputs, "steps");
                    if (st.HasValue) config.steps = Mathf.Max(1, st.Value);
                    float? cf = GetInputFloat(inputs, "cfg");
                    if (cf.HasValue) config.cfg = cf.Value;
                    float? dn = GetInputFloat(inputs, "denoise");
                    if (dn.HasValue) config.denoise = dn.Value;
                    string sn = GetInputString(inputs, "sampler_name");
                    if (!string.IsNullOrEmpty(sn)) config.samplerName = sn;
                    string sc = GetInputString(inputs, "scheduler");
                    if (!string.IsNullOrEmpty(sc)) config.scheduler = sc;
                    break;
                }

                case ComfyNodeType.EmptyLatentImage:
                {
                    int? w = GetInputInt(inputs, "width");
                    if (w.HasValue) config.width = w.Value;
                    int? h = GetInputInt(inputs, "height");
                    if (h.HasValue) config.height = h.Value;
                    int? bs = GetInputInt(inputs, "batch_size");
                    if (bs.HasValue) config.batchSize = bs.Value;
                    break;
                }

                case ComfyNodeType.EmptyLatentAudio:
                {
                    float? sec = GetInputFloat(inputs, "seconds");
                    if (sec.HasValue) config.audioSeconds = sec.Value;
                    break;
                }

                case ComfyNodeType.CheckpointLoader:
                {
                    string ck = GetInputString(inputs, "ckpt_name")
                        ?? GetInputString(inputs, "vae_name")
                        ?? GetInputString(inputs, "model_name")
                        ?? GetInputString(inputs, "unet_name");
                    if (!string.IsNullOrEmpty(ck)) config.checkpointName = ck;
                    break;
                }

                case ComfyNodeType.LoraLoader:
                {
                    string ln = GetInputString(inputs, "lora_name");
                    if (!string.IsNullOrEmpty(ln)) config.loraName = ln;
                    float? sm = GetInputFloat(inputs, "strength_model");
                    if (sm.HasValue) config.strengthModel = sm.Value;
                    float? sc = GetInputFloat(inputs, "strength_clip");
                    if (sc.HasValue) config.strengthClip = sc.Value;
                    break;
                }

                case ComfyNodeType.ImageInput:
                {
                    string img = GetInputString(inputs, "image");
                    if (!string.IsNullOrEmpty(img))
                        config.displayName = "LoadImage: " + img;
                    break;
                }
            }
        }

        // --- MiniJson value helpers ---

        static string GetInputString(Dictionary<string, object> inputs, string key)
        {
            if (inputs.ContainsKey(key) && inputs[key] != null)
                return Convert.ToString(inputs[key], CultureInfo.InvariantCulture);
            return null;
        }

        static long? GetInputLong(Dictionary<string, object> inputs, string key)
        {
            if (!inputs.ContainsKey(key) || inputs[key] == null) return null;
            object v = inputs[key];
            if (v is long l) return l;
            if (v is double d) return (long)d;
            if (v is int i) return (long)i;
            return null;
        }

        static int? GetInputInt(Dictionary<string, object> inputs, string key)
        {
            if (!inputs.ContainsKey(key) || inputs[key] == null) return null;
            object v = inputs[key];
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            if (v is int i) return i;
            return null;
        }

        static float? GetInputFloat(Dictionary<string, object> inputs, string key)
        {
            if (!inputs.ContainsKey(key) || inputs[key] == null) return null;
            object v = inputs[key];
            if (v is long l) return (float)l;
            if (v is double d) return (float)d;
            if (v is int i) return (float)i;
            if (v is float f) return f;
            return null;
        }

        // =========================================================
        // PRESET SAVE / LOAD
        // =========================================================

        void SavePresetDialog()
        {
            string dir = ToFullPath(PresetFolder);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string path = EditorUtility.SaveFilePanel("Save Preset", dir, "preset", "json");
            if (string.IsNullOrEmpty(path)) return;

            SavePreset(path);
        }

        void SavePreset(string path)
        {
            // Sync inputImagePath for all image nodes
            foreach (ComfyNodeConfig node in nodeConfigs)
            {
                if (node.inputImage != null)
                    node.inputImagePath = AssetDatabase.GetAssetPath(node.inputImage);
            }

            ComfyPreset preset = new ComfyPreset
            {
                serverUrl = serverUrl,
                workflowPath = workflowPath,
                outputFolder = outputFolder,
                expectedOutput = (int)expectedOutput,
                nodes = nodeConfigs
            };

            string json = JsonUtility.ToJson(preset, true);
            File.WriteAllText(path, json, Encoding.UTF8);

            // Refresh if inside Assets
            if (path.StartsWith(Application.dataPath))
            {
                string assetPath = "Assets" + path.Substring(Application.dataPath.Length).Replace('\\', '/');
                AssetDatabase.ImportAsset(assetPath);
            }

            status = "Preset saved: " + path;
        }

        void LoadPresetDialog()
        {
            string dir = ToFullPath(PresetFolder);
            if (!Directory.Exists(dir)) dir = Application.dataPath;

            string path = EditorUtility.OpenFilePanel("Load Preset", dir, "json");
            if (string.IsNullOrEmpty(path)) return;

            LoadPreset(path);
        }

        void LoadPreset(string path)
        {
            if (!File.Exists(path))
            {
                status = "Preset file not found.";
                return;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            ComfyPreset preset = JsonUtility.FromJson<ComfyPreset>(json);
            if (preset == null)
            {
                status = "Invalid preset file.";
                return;
            }

            serverUrl = preset.serverUrl;
            if (!string.IsNullOrEmpty(preset.workflowPath))
                workflowPath = preset.workflowPath;
            if (!string.IsNullOrEmpty(preset.outputFolder))
                outputFolder = preset.outputFolder;
            expectedOutput = (ComfyOutputType)preset.expectedOutput;
            nodeConfigs = preset.nodes ?? new List<ComfyNodeConfig>();

            // Restore Texture2D references
            foreach (ComfyNodeConfig node in nodeConfigs)
            {
                if (!string.IsNullOrEmpty(node.inputImagePath))
                    node.inputImage = AssetDatabase.LoadAssetAtPath<Texture2D>(node.inputImagePath);
            }

            workflowAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(workflowPath);
            status = "Preset loaded: " + Path.GetFileName(path);
            Repaint();
        }

        // =========================================================
        // HTTP UTILITIES
        // =========================================================

        static Task<string> PostJson(string url, string json)
        {
            return Task.Run(() =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 30000;
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            });
        }

        static Task<string> GetText(string url)
        {
            return Task.Run(() =>
            {
                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    return client.DownloadString(url);
                }
            });
        }

        static Task<byte[]> DownloadBytes(string url)
        {
            return Task.Run(() =>
            {
                using (WebClient client = new WebClient())
                {
                    return client.DownloadData(url);
                }
            });
        }

        // =========================================================
        // PATH / FOLDER UTILITIES
        // =========================================================

        static string NormalizeServerUrl(string value)
        {
            return string.IsNullOrEmpty(value) ? "http://127.0.0.1:8188" : value.TrimEnd('/');
        }

        static string ToFullPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "";
            if (Path.IsPathRooted(assetPath)) return assetPath;

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        static void EnsureFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder)) return;
            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static string MakeSafeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return string.IsNullOrEmpty(value) ? "comfyui" : value;
        }

        // =========================================================
        // MINI JSON (embedded, no external dependencies)
        // =========================================================

        static class MiniJson
        {
            public static object Deserialize(string json)
            {
                if (json == null) return null;
                return Parser.Parse(json);
            }

            public static string Serialize(object obj)
            {
                return Serializer.Serialize(obj);
            }

            sealed class Parser : IDisposable
            {
                const string WordBreak = "{}[],:\"";
                readonly StringReader json;

                Parser(string jsonString)
                {
                    json = new StringReader(jsonString);
                }

                public static object Parse(string jsonString)
                {
                    using (Parser instance = new Parser(jsonString))
                    {
                        return instance.ParseValue();
                    }
                }

                public void Dispose()
                {
                    json.Dispose();
                }

                Dictionary<string, object> ParseObject()
                {
                    Dictionary<string, object> table = new Dictionary<string, object>();
                    json.Read();

                    while (true)
                    {
                        Token token = NextToken;
                        if (token == Token.None) return null;
                        if (token == Token.CurlyClose) return table;

                        string name = ParseString();
                        if (name == null) return null;
                        if (NextToken != Token.Colon) return null;
                        json.Read();
                        table[name] = ParseValue();
                    }
                }

                List<object> ParseArray()
                {
                    List<object> array = new List<object>();
                    json.Read();

                    bool parsing = true;
                    while (parsing)
                    {
                        Token token = NextToken;
                        switch (token)
                        {
                            case Token.None:
                                return null;
                            case Token.SquaredClose:
                                json.Read();
                                parsing = false;
                                break;
                            default:
                                object value = ParseByToken(token);
                                array.Add(value);
                                break;
                        }
                    }

                    return array;
                }

                object ParseValue()
                {
                    return ParseByToken(NextToken);
                }

                object ParseByToken(Token token)
                {
                    switch (token)
                    {
                        case Token.String:
                            return ParseString();
                        case Token.Number:
                            return ParseNumber();
                        case Token.CurlyOpen:
                            return ParseObject();
                        case Token.SquaredOpen:
                            return ParseArray();
                        case Token.True:
                            return true;
                        case Token.False:
                            return false;
                        case Token.Null:
                            return null;
                        default:
                            return null;
                    }
                }

                string ParseString()
                {
                    StringBuilder s = new StringBuilder();
                    json.Read();

                    bool parsing = true;
                    while (parsing)
                    {
                        if (json.Peek() == -1) break;
                        char c = NextChar;
                        if (c == '"')
                        {
                            parsing = false;
                            break;
                        }

                        if (c == '\\')
                        {
                            if (json.Peek() == -1) parsing = false;
                            c = NextChar;
                            switch (c)
                            {
                                case '"': s.Append('"'); break;
                                case '\\': s.Append('\\'); break;
                                case '/': s.Append('/'); break;
                                case 'b': s.Append('\b'); break;
                                case 'f': s.Append('\f'); break;
                                case 'n': s.Append('\n'); break;
                                case 'r': s.Append('\r'); break;
                                case 't': s.Append('\t'); break;
                                case 'u':
                                    char[] hex = new char[4];
                                    for (int i = 0; i < 4; i++) hex[i] = NextChar;
                                    s.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                            }
                        }
                        else
                        {
                            s.Append(c);
                        }
                    }

                    return s.ToString();
                }

                object ParseNumber()
                {
                    string number = NextWord;
                    if (number.IndexOf('.') == -1 && number.IndexOf('e') == -1 && number.IndexOf('E') == -1)
                    {
                        long parsedInt;
                        if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
                        {
                            return parsedInt;
                        }
                    }

                    double parsedDouble;
                    double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble);
                    return parsedDouble;
                }

                void EatWhitespace()
                {
                    while (char.IsWhiteSpace(PeekChar))
                    {
                        json.Read();
                        if (json.Peek() == -1) break;
                    }
                }

                char PeekChar
                {
                    get { return Convert.ToChar(json.Peek()); }
                }

                char NextChar
                {
                    get { return Convert.ToChar(json.Read()); }
                }

                string NextWord
                {
                    get
                    {
                        StringBuilder word = new StringBuilder();
                        while (json.Peek() != -1 && !char.IsWhiteSpace(PeekChar) && WordBreak.IndexOf(PeekChar) == -1)
                        {
                            word.Append(NextChar);
                        }
                        return word.ToString();
                    }
                }

                Token NextToken
                {
                    get
                    {
                        EatWhitespace();
                        if (json.Peek() == -1) return Token.None;

                        switch (PeekChar)
                        {
                            case '{': return Token.CurlyOpen;
                            case '}':
                                json.Read();
                                return Token.CurlyClose;
                            case '[': return Token.SquaredOpen;
                            case ']': return Token.SquaredClose;
                            case ',':
                                json.Read();
                                return NextToken;
                            case '"': return Token.String;
                            case ':': return Token.Colon;
                            case '0':
                            case '1':
                            case '2':
                            case '3':
                            case '4':
                            case '5':
                            case '6':
                            case '7':
                            case '8':
                            case '9':
                            case '-':
                                return Token.Number;
                        }

                        switch (NextWord)
                        {
                            case "false": return Token.False;
                            case "true": return Token.True;
                            case "null": return Token.Null;
                        }

                        return Token.None;
                    }
                }

                enum Token
                {
                    None,
                    CurlyOpen,
                    CurlyClose,
                    SquaredOpen,
                    SquaredClose,
                    Colon,
                    String,
                    Number,
                    True,
                    False,
                    Null
                }
            }

            sealed class Serializer
            {
                readonly StringBuilder builder;

                Serializer()
                {
                    builder = new StringBuilder();
                }

                public static string Serialize(object obj)
                {
                    Serializer instance = new Serializer();
                    instance.SerializeValue(obj);
                    return instance.builder.ToString();
                }

                void SerializeValue(object value)
                {
                    string str = value as string;
                    IDictionary dict = value as IDictionary;
                    IList list = value as IList;

                    if (value == null) builder.Append("null");
                    else if (str != null) SerializeString(str);
                    else if (value is bool) builder.Append((bool)value ? "true" : "false");
                    else if (list != null) SerializeArray(list);
                    else if (dict != null) SerializeObject(dict);
                    else if (value is char) SerializeString(new string((char)value, 1));
                    else SerializeOther(value);
                }

                void SerializeObject(IDictionary obj)
                {
                    bool first = true;
                    builder.Append('{');
                    foreach (object key in obj.Keys)
                    {
                        if (!first) builder.Append(',');
                        SerializeString(Convert.ToString(key, CultureInfo.InvariantCulture));
                        builder.Append(':');
                        SerializeValue(obj[key]);
                        first = false;
                    }
                    builder.Append('}');
                }

                void SerializeArray(IList array)
                {
                    builder.Append('[');
                    bool first = true;
                    foreach (object obj in array)
                    {
                        if (!first) builder.Append(',');
                        SerializeValue(obj);
                        first = false;
                    }
                    builder.Append(']');
                }

                void SerializeString(string str)
                {
                    builder.Append('"');
                    foreach (char c in str)
                    {
                        switch (c)
                        {
                            case '"': builder.Append("\\\""); break;
                            case '\\': builder.Append("\\\\"); break;
                            case '\b': builder.Append("\\b"); break;
                            case '\f': builder.Append("\\f"); break;
                            case '\n': builder.Append("\\n"); break;
                            case '\r': builder.Append("\\r"); break;
                            case '\t': builder.Append("\\t"); break;
                            default:
                                int codepoint = Convert.ToInt32(c);
                                if (codepoint >= 32 && codepoint <= 126) builder.Append(c);
                                else builder.Append("\\u").Append(codepoint.ToString("x4", CultureInfo.InvariantCulture));
                                break;
                        }
                    }
                    builder.Append('"');
                }

                void SerializeOther(object value)
                {
                    if (value is float || value is double || value is decimal)
                    {
                        builder.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    }
                }
            }
        }
    }
}
