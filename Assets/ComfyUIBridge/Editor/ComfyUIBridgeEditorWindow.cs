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
    public class ComfyUIBridgeEditorWindow : EditorWindow
    {
        const string DefaultWorkflowPath = "Assets/ComfyUIBridge/Workflows/text2image.json";
        const string GeneratedFolder = "Assets/ComfyUIBridge/Generated";

        string serverUrl = "http://127.0.0.1:8188";
        TextAsset workflowAsset;
        string workflowPath = DefaultWorkflowPath;

        string positiveNodeId = "6";
        string negativeNodeId = "7";
        string samplerNodeId = "3";
        string latentNodeId = "5";
        string checkpointNodeId = "4";

        string prompt = "beautiful game environment concept art, painterly, detailed";
        string negativePrompt = "text, watermark, low quality";
        string checkpointName = "sd_xl_base_1.0.safetensors";
        bool overrideCheckpoint;
        bool randomSeed = true;
        long seed = 12345;
        int steps = 20;
        float cfg = 8f;
        int width = 1024;
        int height = 1024;

        bool isGenerating;
        string status = "Ready.";
        string lastPromptId;
        Texture2D previewTexture;
        Vector2 scroll;

        [MenuItem("Tools/ComfyUI Bridge/Generator")]
        public static void Open()
        {
            GetWindow<ComfyUIBridgeEditorWindow>("ComfyUI Bridge");
        }

        void OnEnable()
        {
            workflowAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(DefaultWorkflowPath);
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            serverUrl = EditorGUILayout.TextField("Server URL", serverUrl);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Workflow", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            workflowAsset = (TextAsset)EditorGUILayout.ObjectField("Workflow JSON", workflowAsset, typeof(TextAsset), false);
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

                if (GUILayout.Button("Open Generated Folder"))
                {
                    EnsureFolder(GeneratedFolder);
                    EditorUtility.RevealInFinder(GeneratedFolder);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Node IDs", EditorStyles.boldLabel);
            positiveNodeId = EditorGUILayout.TextField("Positive Text", positiveNodeId);
            negativeNodeId = EditorGUILayout.TextField("Negative Text", negativeNodeId);
            samplerNodeId = EditorGUILayout.TextField("Sampler", samplerNodeId);
            latentNodeId = EditorGUILayout.TextField("Latent Size", latentNodeId);
            checkpointNodeId = EditorGUILayout.TextField("Checkpoint", checkpointNodeId);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prompt", EditorStyles.boldLabel);
            prompt = EditorGUILayout.TextArea(prompt, GUILayout.MinHeight(54));
            EditorGUILayout.LabelField("Negative");
            negativePrompt = EditorGUILayout.TextArea(negativePrompt, GUILayout.MinHeight(38));

            EditorGUILayout.Space();
            overrideCheckpoint = EditorGUILayout.Toggle("Override Checkpoint", overrideCheckpoint);
            using (new EditorGUI.DisabledScope(!overrideCheckpoint))
            {
                checkpointName = EditorGUILayout.TextField("Checkpoint Name", checkpointName);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sampler", EditorStyles.boldLabel);
            randomSeed = EditorGUILayout.Toggle("Random Seed", randomSeed);
            using (new EditorGUI.DisabledScope(randomSeed))
            {
                seed = EditorGUILayout.LongField("Seed", seed);
            }

            steps = EditorGUILayout.IntSlider("Steps", steps, 1, 80);
            cfg = EditorGUILayout.Slider("CFG", cfg, 0f, 20f);
            width = EditorGUILayout.IntField("Width", width);
            height = EditorGUILayout.IntField("Height", height);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(isGenerating))
            {
                if (GUILayout.Button("Generate Image", GUILayout.Height(32)))
                {
                    GenerateImage();
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
                EditorGUILayout.SelectableLabel("Prompt ID: " + lastPromptId, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            if (previewTexture != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);
                float maxWidth = Mathf.Max(220f, position.width - 28f);
                float aspect = previewTexture.height > 0 ? (float)previewTexture.width / previewTexture.height : 1f;
                Rect rect = GUILayoutUtility.GetRect(maxWidth, maxWidth / Mathf.Max(0.01f, aspect), GUILayout.ExpandWidth(true));
                GUI.DrawTexture(rect, previewTexture, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.EndScrollView();
        }

        async void GenerateImage()
        {
            if (isGenerating)
            {
                return;
            }

            isGenerating = true;
            lastPromptId = null;
            status = "Preparing workflow...";
            Repaint();

            try
            {
                string fullPath = ToFullPath(workflowPath);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("Workflow JSON was not found.", fullPath);
                }

                string workflowJson = File.ReadAllText(fullPath, Encoding.UTF8);
                Dictionary<string, object> workflow = MiniJson.Deserialize(workflowJson) as Dictionary<string, object>;
                if (workflow == null)
                {
                    throw new InvalidDataException("Workflow JSON root must be an object.");
                }

                ApplyWorkflowOverrides(workflow);

                string clientId = Guid.NewGuid().ToString("N");
                Dictionary<string, object> request = new Dictionary<string, object>
                {
                    { "client_id", clientId },
                    { "prompt", workflow }
                };

                status = "Queueing prompt...";
                Repaint();

                string promptResponse = await PostJson(NormalizeServerUrl(serverUrl) + "/prompt", MiniJson.Serialize(request));
                Dictionary<string, object> promptData = MiniJson.Deserialize(promptResponse) as Dictionary<string, object>;
                if (promptData == null || !promptData.ContainsKey("prompt_id"))
                {
                    throw new InvalidDataException("ComfyUI did not return a prompt_id: " + promptResponse);
                }

                lastPromptId = Convert.ToString(promptData["prompt_id"], CultureInfo.InvariantCulture);
                status = "Waiting for ComfyUI...";
                Repaint();

                ComfyOutput output = await WaitForImageOutput(lastPromptId);
                status = "Downloading " + output.filename + "...";
                Repaint();

                byte[] png = await DownloadBytes(BuildViewUrl(output));
                EnsureFolder(GeneratedFolder);
                string safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(output.filename));
                string assetPath = GeneratedFolder + "/" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + safeName + Path.GetExtension(output.filename);
                File.WriteAllBytes(ToFullPath(assetPath), png);
                AssetDatabase.ImportAsset(assetPath);
                previewTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                Selection.activeObject = previewTexture;
                status = "Saved: " + assetPath;
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

        void ApplyWorkflowOverrides(Dictionary<string, object> workflow)
        {
            long seedToUse = randomSeed ? CreateRandomSeed() : seed;
            seed = seedToUse;

            SetNodeInput(workflow, positiveNodeId, "text", prompt);
            SetNodeInput(workflow, negativeNodeId, "text", negativePrompt);

            SetNodeInput(workflow, samplerNodeId, "seed", seedToUse);
            SetNodeInput(workflow, samplerNodeId, "noise_seed", seedToUse, false);
            SetNodeInput(workflow, samplerNodeId, "steps", Mathf.Max(1, steps));
            SetNodeInput(workflow, samplerNodeId, "cfg", cfg);

            SetNodeInput(workflow, latentNodeId, "width", Mathf.Max(64, width));
            SetNodeInput(workflow, latentNodeId, "height", Mathf.Max(64, height));
            SetNodeInput(workflow, latentNodeId, "batch_size", 1, false);

            if (overrideCheckpoint)
            {
                SetNodeInput(workflow, checkpointNodeId, "ckpt_name", checkpointName);
            }
        }

        static void SetNodeInput(Dictionary<string, object> workflow, string nodeId, string inputName, object value, bool warnIfMissing = true)
        {
            if (string.IsNullOrEmpty(nodeId) || !workflow.ContainsKey(nodeId))
            {
                if (warnIfMissing) Debug.LogWarning("[ComfyUI Bridge] Node not found: " + nodeId);
                return;
            }

            Dictionary<string, object> node = workflow[nodeId] as Dictionary<string, object>;
            Dictionary<string, object> inputs = node != null && node.ContainsKey("inputs") ? node["inputs"] as Dictionary<string, object> : null;
            if (inputs == null)
            {
                if (warnIfMissing) Debug.LogWarning("[ComfyUI Bridge] Node has no inputs: " + nodeId);
                return;
            }

            if (!inputs.ContainsKey(inputName) && !warnIfMissing)
            {
                return;
            }

            inputs[inputName] = value;
        }

        async Task<ComfyOutput> WaitForImageOutput(string promptId)
        {
            string historyUrl = NormalizeServerUrl(serverUrl) + "/history/" + Uri.EscapeDataString(promptId);
            DateTime timeout = DateTime.UtcNow.AddMinutes(10);

            while (DateTime.UtcNow < timeout)
            {
                await Task.Delay(1000);
                string historyJson = await GetText(historyUrl);
                Dictionary<string, object> history = MiniJson.Deserialize(historyJson) as Dictionary<string, object>;
                ComfyOutput output = TryFindImageOutput(history, promptId);
                if (output != null)
                {
                    return output;
                }

                status = "Waiting for ComfyUI... " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                Repaint();
            }

            throw new TimeoutException("Timed out waiting for ComfyUI history output.");
        }

        static ComfyOutput TryFindImageOutput(Dictionary<string, object> history, string promptId)
        {
            if (history == null || !history.ContainsKey(promptId))
            {
                return null;
            }

            Dictionary<string, object> item = history[promptId] as Dictionary<string, object>;
            Dictionary<string, object> outputs = item != null && item.ContainsKey("outputs") ? item["outputs"] as Dictionary<string, object> : null;
            if (outputs == null)
            {
                return null;
            }

            foreach (object nodeValue in outputs.Values)
            {
                Dictionary<string, object> node = nodeValue as Dictionary<string, object>;
                if (node == null) continue;

                foreach (object value in node.Values)
                {
                    List<object> list = value as List<object>;
                    if (list == null || list.Count == 0) continue;

                    Dictionary<string, object> file = list[0] as Dictionary<string, object>;
                    if (file == null || !file.ContainsKey("filename")) continue;

                    string filename = Convert.ToString(file["filename"], CultureInfo.InvariantCulture);
                    string ext = Path.GetExtension(filename).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                    return new ComfyOutput
                    {
                        filename = filename,
                        subfolder = file.ContainsKey("subfolder") ? Convert.ToString(file["subfolder"], CultureInfo.InvariantCulture) : "",
                        type = file.ContainsKey("type") ? Convert.ToString(file["type"], CultureInfo.InvariantCulture) : "output"
                    };
                }
            }

            return null;
        }

        string BuildViewUrl(ComfyOutput output)
        {
            return NormalizeServerUrl(serverUrl)
                + "/view?filename=" + Uri.EscapeDataString(output.filename)
                + "&subfolder=" + Uri.EscapeDataString(output.subfolder ?? "")
                + "&type=" + Uri.EscapeDataString(string.IsNullOrEmpty(output.type) ? "output" : output.type);
        }

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

        static string NormalizeServerUrl(string value)
        {
            return string.IsNullOrEmpty(value) ? "http://127.0.0.1:8188" : value.TrimEnd('/');
        }

        static long CreateRandomSeed()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            long value = BitConverter.ToInt64(bytes, 0);
            return Math.Abs(value % 9000000000000000L) + 1L;
        }

        static string ToFullPath(string assetPath)
        {
            if (Path.IsPathRooted(assetPath))
            {
                return assetPath;
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        static void EnsureFolder(string assetFolder)
        {
            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        static string MakeSafeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return string.IsNullOrEmpty(value) ? "comfyui" : value;
        }

        class ComfyOutput
        {
            public string filename;
            public string subfolder;
            public string type;
        }

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
