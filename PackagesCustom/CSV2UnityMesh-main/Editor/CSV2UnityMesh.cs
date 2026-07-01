using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using System.IO;
using System.Linq;
using System;
using System.Text.RegularExpressions;

namespace UnityEditor.CSV2UnityMesh
{
    //1111Unity因为对顶点支持的问题不能超过65000顶点，但是由于csv的顶点会重复，
    //大概只能支持单2W面以内，则会破损建议用外部工具导出。
    //在导出车辆模型时遇到破面发现时因为这个问题，非精度问题(想尝试double和vector3d)。
    public class CSV2UnityMesh : EditorWindow
    {
        private SerializedObject serializedObject;
        private SerializedProperty csvAssetProp;
        private SerializedProperty csvPVProp;
        private SerializedProperty csvVMProp;
        private SerializedProperty m_materixVP;
        private SerializedProperty m_materixM;
        private SerializedProperty materialDebugModeProp;

        public static int positionColumnID = 3;
        public static int p_positionColumnID = 3;
        public static int normalColumnID = 6;
        public static int tangentColumnID = 9;
        public static int colorColumnID = 13;
        public static int[] texcoordColumnID = new int[] {17 , 19, 21, 23, 25};

        public static float modelScale = 1.0f;
        public static bool flipNormals = false;
        public static bool decodeNormals = false;
        public static bool flipUV = false;
        public static bool[] enableTexcoord = new bool[] {true, false, false, false, false};
        public static bool[] enableTexcoordUVChange = new bool[] {false, false, false, false, false};

        private string[] m_columnHeadsArray = null;
        private string[] m_p_columnHeadsArray = null;

        public TextAsset m_csvAsset = null;

        public TextAsset m_csvPAsset = null;
        public TextAsset m_csvVMAsset = null;
        public Matrix4x4 MatrixVP;
        public Matrix4x4 MatrixM;
        

        
        public static ExportFormat fbxExportFormat = ExportFormat.ASCII;
        public static string[] fbxFormatDisplayedString =
        {
            "ASCII",
            "Binary",
        };

        private string m_outFilePath = "Assets/CSV2UnityMesh/";
        private string m_outFileName = "outfile.fbx";

        private static readonly GUID debugShaderGUID = new GUID("86e38963fcf6c9d47952280214f1d1c1");
        private static readonly string debugMaterialGUID = ("a97c6f0fc94b8c14e979667a1dcc2dda");
        private static Material debugMaterial;

        // Preview
        private PreviewRenderUtility previewRenderUtility;
        private Mesh targetMesh;
        private Vector2 previewDir = new Vector2(120, -20);
        private float zoom = 5f;
        private Vector3 objectPosition = Vector3.zero;
        private Vector2 dragStartPos;
        public enum MaterialDebugMode
        {
            BasicLighting,
            Normal,
            Tangent,
            VertexColor,
            Texcoord0,
            Texcoord1,
            Texcoord2,
            Texcoord3,
            Texcoord4
        }
        public MaterialDebugMode m_materialDebugMode;
        public enum MaterialOutputChannel
        {
            None = 0,
            Red = 1 << 0,    // 1
            Green = 1 << 1,  // 2
            Blue = 1 << 2,   // 4
            Alpha = 1 << 3   // 8
        }
        public MaterialOutputChannel m_materialOutputChannel = (MaterialOutputChannel)7;

        [MenuItem("Tools/CSV2UnityMesh")]
        public static void ShowWindow()
        {
            var window = EditorWindow.GetWindow(typeof(CSV2UnityMesh));
            window.position = new Rect(800, 300, 500, 809);
        }

        private void OnEnable()
        {
            ReadCSVAssetToHeadsArray(m_csvAsset, out m_columnHeadsArray, out m_outFileName);

            serializedObject = new SerializedObject(this);
            csvAssetProp = serializedObject.FindProperty("m_csvAsset");
            csvPVProp = serializedObject.FindProperty("m_csvPAsset");
            csvVMProp = serializedObject.FindProperty("m_csvVMAsset");
            m_materixVP = serializedObject.FindProperty("MatrixVP");
            m_materixM = serializedObject.FindProperty("MatrixM");
            materialDebugModeProp = serializedObject.FindProperty("m_materialDebugMode");
            
            debugMaterial = (Material)AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(debugMaterialGUID), typeof(Material));

            previewRenderUtility = new PreviewRenderUtility();
            previewRenderUtility.cameraFieldOfView = 30f;
            previewRenderUtility.camera.transform.position = new Vector3(0, 0, -zoom);
            previewRenderUtility.camera.transform.LookAt(Vector3.zero);
            previewRenderUtility.camera.farClipPlane = 1000.0f;
            previewRenderUtility.camera.nearClipPlane = 0.03f;
        }

        private void OnGUI()
        {
            serializedObject.Update();
            GUILayout.Space(10);

            float tempLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 100;

            // csvAsset
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(Styles.csvAsset, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(csvAssetProp);
            GUILayout.Space(10);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                ReadCSVAssetToHeadsArray(m_csvAsset, out m_columnHeadsArray, out m_outFileName);
                targetMesh = null;
            }
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(Styles.csvPAsset, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(csvPVProp);
            GUILayout.Space(10);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                string filename;
                ReadCSVAssetToHeadsArray(m_csvPAsset, out m_p_columnHeadsArray, out filename);
            }
            if(m_csvPAsset!=null)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.LabelField(Styles.csvVMAsset, EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(csvVMProp);
                GUILayout.Space(1);
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    string filename;
                    float[][] vp4;
                    float[][] m4;
                    ReadVMatrixCSVAsset(m_csvVMAsset, out vp4, out m4);
                    MatrixVP.m00 = vp4[0][0];
                    MatrixVP.m10 = vp4[0][1];
                    MatrixVP.m20 = vp4[0][2];
                    MatrixVP.m30 = vp4[0][3];
                    MatrixVP.m01 = vp4[1][0];
                    MatrixVP.m11 = vp4[1][1];
                    MatrixVP.m21 = vp4[1][2];
                    MatrixVP.m31 = vp4[1][3];
                    MatrixVP.m02 = vp4[2][0];
                    MatrixVP.m12 = vp4[2][1];
                    MatrixVP.m22 = vp4[2][2];
                    MatrixVP.m32 = vp4[2][3];
                    MatrixVP.m03 = vp4[3][0];
                    MatrixVP.m13 = vp4[3][1];
                    MatrixVP.m23 = vp4[3][2];
                    MatrixVP.m33 = vp4[3][3];
                    
                    MatrixM.m00 = m4[0][0];
                    MatrixM.m10 = m4[0][1];
                    MatrixM.m20 = m4[0][2];
                    MatrixM.m30 = m4[0][3];
                    MatrixM.m01 = m4[1][0];
                    MatrixM.m11 = m4[1][1];
                    MatrixM.m21 = m4[1][2];
                    MatrixM.m31 = m4[1][3];
                    MatrixM.m02 = m4[2][0];
                    MatrixM.m12 = m4[2][1];
                    MatrixM.m22 = m4[2][2];
                    MatrixM.m32 = m4[2][3];
                    MatrixM.m03 = m4[3][0];
                    MatrixM.m13 = m4[3][1];
                    MatrixM.m23 = m4[3][2];
                    MatrixM.m33 = m4[3][3];
                }
                
                EditorGUILayout.LabelField(Styles.matrixVP, EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_materixVP);
                EditorGUILayout.LabelField(Styles.matrixM, EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_materixM);
            }
            GUI.enabled = m_csvAsset != null;
            
            
            // Mesh properties
            EditorGUILayout.LabelField(Styles.meshProperties, EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical();

            EditorGUILayout.BeginHorizontal();
            if (m_csvPAsset != null)
            {
                p_positionColumnID = EditorGUILayout.Popup(Styles.positionStr, p_positionColumnID, m_p_columnHeadsArray,
                    EditorStyles.popup, GUILayout.Width(250)); 
            }
            else
            {
                positionColumnID = EditorGUILayout.Popup(Styles.positionStr, positionColumnID, m_columnHeadsArray,
                    EditorStyles.popup, GUILayout.Width(250));
            }
            GUILayout.FlexibleSpace();
            modelScale = EditorGUILayout.FloatField(Styles.scale, modelScale, GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            normalColumnID      = EditorGUILayout.Popup(Styles.normalStr, normalColumnID    , m_columnHeadsArray, EditorStyles.popup, GUILayout.Width(250));
            GUILayout.FlexibleSpace(); 
            flipNormals = EditorGUILayout.Toggle(Styles.flipNormal, flipNormals, GUILayout.Width(150)) ;
            decodeNormals = EditorGUILayout.Toggle(Styles.decodeNormal, decodeNormals, GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();

            tangentColumnID     = EditorGUILayout.Popup(Styles.tangentStr, tangentColumnID  , m_columnHeadsArray, EditorStyles.popup, GUILayout.Width(250));
            colorColumnID       = EditorGUILayout.Popup(Styles.colorStr, colorColumnID      , m_columnHeadsArray, EditorStyles.popup, GUILayout.Width(250));
      
            for (int ti = 0; ti < texcoordColumnID.Length; ti++)
            {
                EditorGUILayout.BeginHorizontal();
                if (enableTexcoord[ti])
                {
                    texcoordColumnID[ti] = EditorGUILayout.Popup(Styles.texcoordStr + ti + ":", texcoordColumnID[ti], m_columnHeadsArray, EditorStyles.popup, GUILayout.Width(250));
                }
                else
                {
                    var tempEnable = GUI.enabled;
                    GUI.enabled = false;
                    texcoordColumnID[ti] = EditorGUILayout.Popup(Styles.texcoordStr + ti + ":", texcoordColumnID[ti], m_columnHeadsArray, EditorStyles.popup, GUILayout.Width(250));
                    GUI.enabled = tempEnable;
                }
                GUILayout.FlexibleSpace(); enableTexcoordUVChange[ti] = EditorGUILayout.Toggle(Styles.enableTexcoordUVChange, enableTexcoordUVChange[ti], GUILayout.Width(150));
                GUILayout.FlexibleSpace(); enableTexcoord[ti] = EditorGUILayout.Toggle(Styles.enableTexcoord, enableTexcoord[ti], GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();
            }


            EditorGUIUtility.labelWidth = tempLabelWidth;
            EditorGUILayout.EndVertical();


            #region Preview Mesh

            if (targetMesh != null)
            {
                GUILayout.Space(20);

                Rect previewRect = GUILayoutUtility.GetRect(200, 200, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

                // Handle mouse events for rotation
                HandleMouseEvents(previewRect);


                previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);

                // Configure the camera and render the mesh
                Matrix4x4 trs = Matrix4x4.TRS(objectPosition, Quaternion.identity, Vector3.one * 5);
                previewRenderUtility.camera.transform.position = Quaternion.Euler(previewDir.y, previewDir.x, 0) * new Vector3(0, 0, -zoom);
                previewRenderUtility.camera.transform.LookAt(Vector3.zero);
                previewRenderUtility.DrawMesh(targetMesh, trs, debugMaterial, 0);
                previewRenderUtility.camera.Render();

                //Texture resultRender = previewRenderUtility.EndPreview();
                //GUI.DrawTexture(previewRect, resultRender, ScaleMode.StretchToFill, false);

                previewRenderUtility.EndAndDrawPreview(previewRect);
            }
            GUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(materialDebugModeProp);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();

                SetMaterialDebugMode(debugMaterial, m_materialDebugMode);
            }

            GUILayout.Space(50);

            uint channelMask = (uint)m_materialOutputChannel;
            if (GUILayout.Button("R", GetButtonStyle((channelMask & (1 << 0)) != 0), GUILayout.Width(30)))
            {
                m_materialOutputChannel = (MaterialOutputChannel)(channelMask ^ (1 << 0));
                SetMaterialOutPutChannel(debugMaterial, m_materialOutputChannel);
            }

            if (GUILayout.Button("G", GetButtonStyle((channelMask & (1 << 1)) != 0), GUILayout.Width(30)))
            {
                m_materialOutputChannel = (MaterialOutputChannel)(channelMask ^ (1 << 1));
                SetMaterialOutPutChannel(debugMaterial, m_materialOutputChannel);
            }

            if (GUILayout.Button("B", GetButtonStyle((channelMask & (1 << 2)) != 0), GUILayout.Width(30)))
            {
                m_materialOutputChannel = (MaterialOutputChannel)(channelMask ^ (1 << 2));
                SetMaterialOutPutChannel(debugMaterial, m_materialOutputChannel);
            }

            if (GUILayout.Button("A", GetButtonStyle((channelMask & (1 << 3)) != 0), GUILayout.Width(30)))
            {
                m_materialOutputChannel = (MaterialOutputChannel)(channelMask ^ (1 << 3));
                SetMaterialOutPutChannel(debugMaterial, m_materialOutputChannel);
            }

            GUILayout.EndHorizontal();

            #endregion Preview Mesh


            #region SaveFileGUI
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Styles.fbxFormatStr, EditorStyles.boldLabel, GUILayout.Width(80));
            fbxExportFormat = (ExportFormat)EditorGUILayout.Popup((int)fbxExportFormat, fbxFormatDisplayedString, EditorStyles.popup, GUILayout.Width(200));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("SavePath: ", EditorStyles.boldLabel, GUILayout.Width(80));
            EditorGUILayout.TextArea(m_outFilePath, GUILayout.Width(200));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("OutName: ", EditorStyles.boldLabel, GUILayout.Width(80));
            EditorGUILayout.TextArea(m_outFileName, GUILayout.Width(200));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();


            if (GUILayout.Button("Select Path", GUILayout.Height(40)))
            {
                string selectedPath = EditorUtility.SaveFolderPanel("Select an output path", "Assets", "defaultFold");
                if (!String.IsNullOrEmpty(selectedPath))
                {
                    selectedPath = ConvertToRelativePath(selectedPath);
                    m_outFilePath = selectedPath + "/";
                }
            }

            GUILayout.EndHorizontal();
            #endregion


            GUILayout.Space(10);
            if (GUILayout.Button("Generate Mesh", GUILayout.Height(30)))
            {

                Matrix4x4 matrixMVPInv = MatrixVP*MatrixM;
                matrixMVPInv = matrixMVPInv.inverse;
                Mesh genMesh = CreateMeshFromCSVAsset(m_columnHeadsArray, m_csvAsset, m_p_columnHeadsArray,m_csvPAsset, matrixMVPInv, false, false,flipNormals,decodeNormals);
                if (genMesh != null)
                {
                    CreateMeshAssetAndShow(genMesh, m_outFilePath, m_outFileName);
                    //GameObject.Destroy(genMesh);

                    string exportPath = Path.Combine(m_outFilePath, m_outFileName);
                    targetMesh = AssetDatabase.LoadAssetAtPath<Mesh>(exportPath);
                    SetMaterialDebugMode(debugMaterial, m_materialDebugMode);
                }
            }

            GUI.enabled = true;

            GUILayout.Space(30);
            serializedObject.ApplyModifiedProperties();
        }

        private GUIStyle GetButtonStyle(bool active)
        {
            GUIStyle style = new GUIStyle(GUI.skin.box);
            if (active)
            {
                style.normal.background = Texture2D.whiteTexture;
            }
            else
            {
                style.normal.textColor = Color.black;
                style.active.textColor = Color.black;
                style.focused.textColor = Color.black;
                style.hover.textColor = Color.black;
            }
            return style;
        }

        private static void ReadVMatrixCSVAsset(TextAsset vmCsvAsset,out float[][] vp4, out float[][] m4)
        {
            vp4 = new float[4][];
            m4 = new float[4][];
            for (int i = 0; i < vp4.Length; i++)
            {
                vp4[i] = new float[4];
            }
            for (int i = 0; i < m4.Length; i++)
            {
                m4[i] = new float[4];
            }
            if (vmCsvAsset != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(vmCsvAsset);
                if (!System.IO.File.Exists(assetPath))
                    return ;
        
                string clipboard = System.IO.File.ReadAllText(assetPath);
                var allTexts = clipboard.Split('\n');
                
                for (int i = 0; i < allTexts.Length; i++)
                {
                    var contentes = allTexts[i].Trim().Replace(" ", "").Split(',');
        
                    if (contentes.Length <=1)
                        continue;
                    if (contentes[0].Length > Styles.matrixVPStr.Length)
                    {
                        if(contentes[0].Contains(Styles.matrixVPStr))
                        {
                            MatchCollection  use =Regex.Matches(contentes[0], Styles.tiquStr);
                            int index = int.Parse(use[0].Groups[1].Value);
                            //var nums = contentes[1].Split(',');
                            Debug.Log(index);
                            for (int j = 0; j < vp4[index].Length; j++)
                            {
                                
                                Debug.Log(contentes[j+1]);
                                string usebuff = contentes[j + 1];
                                if (j == 0)
                                {
                                    usebuff = usebuff.Substring(1);
                                }
                                else if (j == 3)
                                {
                                    usebuff = usebuff.Substring(0,contentes[j+1].Length-1);
                                }
                                vp4[index][j]= float.Parse(usebuff);
                            }
                        }
                    }
                    if (contentes[0].Length > Styles.matrixMStr.Length)
                    {
                        if(contentes[0].Contains(Styles.matrixMStr))
                        {
                            MatchCollection  use =Regex.Matches(contentes[0], Styles.tiquStr);
                            int index = int.Parse(use[0].Groups[1].Value);
                            for (int j = 0; j < m4[index].Length; j++)
                            {
                                string usebuff = contentes[j + 1];
                                if (j == 0)
                                {
                                    usebuff = usebuff.Substring(1);
                                }
                                else if (j == 3)
                                {
                                    usebuff = usebuff.Substring(0,contentes[j+1].Length-1);
                                }
                                m4[index][j]= float.Parse(usebuff);
                            }
                        }
                    }
                }
            }
        }
        private static void ReadCSVAssetToHeadsArray(TextAsset csvAsset, out string[] columnHeadsArray, out string fileName)
        {
            if (csvAsset != null)
            {
                // Read asset to columnHeadsArray
                var dataHeads = ReadCSVColumnHeads(csvAsset);
                columnHeadsArray = dataHeads.ToArray();
                fileName = csvAsset.name + ".fbx";
            }
            else
            {
                columnHeadsArray = new string[]
                {
                    Styles.VTXStr, Styles.IDXStr,
                    Styles.positionStr, Styles.positionStr, Styles.positionStr,
                    Styles.normalStr, Styles.normalStr, Styles.normalStr,
                    Styles.tangentStr, Styles.tangentStr, Styles.tangentStr, Styles.tangentStr,
                    Styles.colorStr, Styles.colorStr, Styles.colorStr, Styles.colorStr,
                    Styles.texcoordStr + "0", Styles.texcoordStr + "0",
                    Styles.texcoordStr + "1", Styles.texcoordStr + "1",
                    Styles.texcoordStr + "2", Styles.texcoordStr + "2",
                    Styles.texcoordStr + "3", Styles.texcoordStr + "3",
                    Styles.texcoordStr + "4", Styles.texcoordStr + "4",
                };
                fileName = "noneFileName.fbx";
            }
        }

        public static List<string> ReadCSVColumnHeads(TextAsset csvAsset)
        {
            string assetPath = AssetDatabase.GetAssetPath(csvAsset);
            if (!System.IO.File.Exists(assetPath))
                return null;

            string clipboard = System.IO.File.ReadAllText(assetPath);
            var allTexts = clipboard.Split('\n');

            var heads = allTexts[0].Trim().Replace(" ", "").Split(',');

            return heads.Select(key => key.Contains(".") ? key.Split('.')[0] : key).ToList();
        }

        /// <summary>
        /// 重叠顶点合并
        /// </summary>
        /// <param name="mesh"></param>
        /// <returns></returns>
        Mesh GetWeldMesh(Mesh mesh)
        {
            Vector3[] vs = mesh.vertices;
            int[] ts = mesh.triangles;
            Debug.Log(ts.Length);
            Dictionary<Vector3, int> dicVert = new Dictionary<Vector3, int>();
            for (int i = 0; i < ts.Length; i++)
            {
                bool hasSamePos = false;
                Vector3 vert = vs[ts[i]];
                foreach (var kv in dicVert)
                {
                    Vector3 key = kv.Key;
                    if (vert == key)
                    {
                        if (kv.Value < ts[i])
                        {
                            ts[i] = kv.Value;
                        }
                        else
                        {
                            dicVert[kv.Key] = ts[i];
                        }
                        hasSamePos = true;
                        break;
                    }
                }
                if (!hasSamePos)
                {
                    dicVert.Add(vert, ts[i]);
                }
            }
            List<Vector3> listVertice = new List<Vector3>();
            foreach (var kv in dicVert)
            {
                listVertice.Add(kv.Key);
            }
            Mesh meshNew = new Mesh()
            {
                vertices = listVertice.ToArray(),
                triangles = ts
            };
            meshNew.RecalculateNormals();
            return meshNew;
        }
        public static Mesh CreateMeshFromCSVAsset( string[] csvColumnHeads, TextAsset csvAsset,string[] p_csvColumnHeads, TextAsset csvPAsset,Matrix4x4 matrixMVPInv, bool rotation90 = false, bool flipUV = false, bool flipNormals = false,bool decodeNormals =false,bool texcoord0XYChange = false)
        {
            List<float[]> p_allRows = new List<float[]>();
            int p_positionColumnX=0;
            int p_positionColumnY=0;
            int p_positionColumnZ=0;
            int p_positionColumnW=0;

            bool isUsedPAsset = false;
            if (csvPAsset != null)
            {
                var ppath = AssetDatabase.GetAssetPath(csvPAsset);
                if (!System.IO.File.Exists(ppath))
                {
                    Debug.LogError("No csv P files at path: " + ppath);
                    return null;
                }
                string p_clipboard = System.IO.File.ReadAllText(ppath);
                var p_allTexts = p_clipboard.Split('\n');

                if (p_allTexts.Length <= 1)
                {
                    Debug.LogError("Csv P files length flase: " + p_allTexts.Length);
                    return null;
                }
                var p_heads = p_allTexts[0].Trim().Replace(" ", "").Split(',');
                
                ReadAllRows(p_allTexts, p_heads.Length, ref p_allRows);
                
                //var p_IDX = GetColumnIndex(heads, "IDX");
                p_positionColumnX = GetColumnIndex(p_heads, p_csvColumnHeads[p_positionColumnID] + ".x");
                p_positionColumnY = GetColumnIndex(p_heads, p_csvColumnHeads[p_positionColumnID] + ".y");
                p_positionColumnZ = GetColumnIndex(p_heads, p_csvColumnHeads[p_positionColumnID] + ".z");
                p_positionColumnW = GetColumnIndex(p_heads, p_csvColumnHeads[p_positionColumnID] + ".w");
                
                Debug.Log("p_positionColumnX:" + p_positionColumnX);
                Debug.Log("p_positionColumnY:" + p_positionColumnY);
                Debug.Log("p_positionColumnY:" + p_positionColumnY);
                Debug.Log("p_positionColumnW:" + p_positionColumnW);
                isUsedPAsset = true;
            }
            var assetPath = AssetDatabase.GetAssetPath(csvAsset);
            if (!System.IO.File.Exists(assetPath))
            {
                Debug.LogError("No csv files at path: " + assetPath);
                return null;
            }

            string clipboard = System.IO.File.ReadAllText(assetPath);
            var allTexts = clipboard.Split('\n');

            if (allTexts.Length <= 1)
            {
                Debug.LogError("Csv files length flase: " + allTexts.Length);
                return null;
            }
            var heads = allTexts[0].Trim().Replace(" ", "").Split(',');
            List<float[]> allRows = new List<float[]>();
            ReadAllRows(allTexts, heads.Length, ref allRows);

            var IDX = GetColumnIndex(heads, "IDX");
            int positionColumnX = 0;
            int positionColumnY = 0;
            int positionColumnZ = 0;
            if (!isUsedPAsset)
            {
                positionColumnX = GetColumnIndex(heads, csvColumnHeads[positionColumnID] + ".x");
                positionColumnY = GetColumnIndex(heads, csvColumnHeads[positionColumnID] + ".y");
                positionColumnZ = GetColumnIndex(heads, csvColumnHeads[positionColumnID] + ".z");
            }

            var normalColumnX = GetColumnIndex(heads, csvColumnHeads[normalColumnID] + ".x");
            var normalColumnY = GetColumnIndex(heads, csvColumnHeads[normalColumnID] + ".y");
            var normalColumnZ = GetColumnIndex(heads, csvColumnHeads[normalColumnID] + ".z");

            var tangentColumnX = GetColumnIndex(heads, csvColumnHeads[tangentColumnID] + ".x");
            var tangentColumnY = GetColumnIndex(heads, csvColumnHeads[tangentColumnID] + ".y");
            var tangentColumnZ = GetColumnIndex(heads, csvColumnHeads[tangentColumnID] + ".z");
            var tangentColumnW = GetColumnIndex(heads, csvColumnHeads[tangentColumnID] + ".w");

            var colorColumnX = GetColumnIndex(heads, csvColumnHeads[colorColumnID] + ".x");
            var colorColumnY = GetColumnIndex(heads, csvColumnHeads[colorColumnID] + ".y");
            var colorColumnZ = GetColumnIndex(heads, csvColumnHeads[colorColumnID] + ".z");
            var colorColumnW = GetColumnIndex(heads, csvColumnHeads[colorColumnID] + ".w");

            int[] texcoordColumnX = new int[] { -1, -1, -1, -1, -1 };
            int[] texcoordColumnY = new int[] { -1, -1, -1, -1, -1 };
            int[] texcoordColumnZ = new int[] { -1, -1, -1, -1, -1 };
            int[] texcoordColumnW = new int[] { -1, -1, -1, -1, -1 };

            for ( int ti = 0; ti < texcoordColumnID.Length; ti++ )
            {
                if (!enableTexcoord[ti])
                    continue;
                if (!enableTexcoordUVChange[ti])
                {
                    texcoordColumnX[ti] = GetColumnIndex(heads, csvColumnHeads[texcoordColumnID[ti]] + ".x");
                    texcoordColumnY[ti] = GetColumnIndex(heads, csvColumnHeads[texcoordColumnID[ti]] + ".y");
                }
                else
                {
                    texcoordColumnX[ti] = GetColumnIndex(heads, csvColumnHeads[texcoordColumnID[ti]] + ".y");
                    texcoordColumnY[ti] = GetColumnIndex(heads, csvColumnHeads[texcoordColumnID[ti]] + ".x");
                }

                texcoordColumnZ[ti] = GetColumnIndex(heads, csvColumnHeads[texcoordColumnID[ti]] + ".z");
                texcoordColumnW[ti] = GetColumnIndex(heads, csvColumnHeads[texcoordColumnID[ti]] + ".w");
                
            }


            if (IDX < 0 || positionColumnX < 0 || positionColumnY < 0 || positionColumnZ < 0)
            {
                Debug.Log("Position data error.");
                return null;
            }
            bool hasNormalProp = (normalColumnX >= 0 && normalColumnY >= 0 && normalColumnZ >= 0);
            bool hasTangentProp = (tangentColumnX >= 0 && tangentColumnY >= 0 && tangentColumnZ >= 0 && tangentColumnW >= 0);
            bool hasColorProp = (colorColumnX >= 0 && colorColumnY >= 0 && colorColumnZ >= 0 && colorColumnW >= 0);

            int minIndex = int.MaxValue;
            int maxIndex = -1;
            for (int i = 0; i < allRows.Count; ++i)
            {
                int currIndex = (int)allRows[i][IDX];
                if (currIndex < minIndex)
                {
                    minIndex = currIndex;
                }
                else if (currIndex > maxIndex)
                {
                    maxIndex = currIndex;
                }
            }

            int vertexLength = maxIndex - minIndex + 1; // Container Self Index.
            int indexLen = allRows.Count;
            if (indexLen % 3 != 0)
            {
                Debug.Log("vertex Length is zero.");
                return null;
            }

            Vector3[] vertices     = new Vector3[vertexLength];
            Vector3[] normals       = new Vector3[vertexLength];
            Vector4[] tangents      = new Vector4[vertexLength];
            Color[]   vertexColors  = new Color[vertexLength];
            List<Vector4[]> vertexTexcoords = new List<Vector4[]>();
            for (int ti = 0; ti < texcoordColumnID.Length; ti++)
            {
                if (!enableTexcoord[ti])
                    continue;
                vertexTexcoords.Add(new Vector4[vertexLength]);
            }

            int[] outputIndexBuff = new int[indexLen];
            var rotationN90 = rotation90 ? Quaternion.Euler(-90, 0, 0) : Quaternion.identity;
            for (int i = 0; i < allRows.Count; ++i)
            {
                var currLine = allRows[i];
                var realIndex = (int)currLine[IDX] - minIndex;
                outputIndexBuff[i] = realIndex;
                if (realIndex < vertices.Length && realIndex >= 0)
                {
                    var p = Vector3.zero;
                    if (isUsedPAsset)
                    {
                        var buff = p_allRows[i];
                        var clipPos = new Vector4(buff[p_positionColumnX], buff[p_positionColumnY], buff[p_positionColumnZ], buff[p_positionColumnW]);
                        p = (matrixMVPInv * clipPos);
                    }
                    else
                    {
                        p = new Vector3(currLine[positionColumnX], currLine[positionColumnY],
                            currLine[positionColumnZ]);
                    }
                    // var p = new Vector3(currLine[positionColumnX], currLine[positionColumnY],
                    //     currLine[positionColumnZ]);
                    vertices[realIndex] = rotationN90 * p;

                    vertices[realIndex].x *= modelScale;
                    vertices[realIndex].y *= modelScale;
                    vertices[realIndex].z *= modelScale;

                    if (hasNormalProp)
                    {
                        var nor = new Vector3(currLine[normalColumnX], currLine[normalColumnY], currLine[normalColumnZ]);
                        normals[realIndex] = rotationN90 * nor;
                    }

                    if (hasTangentProp)
                    {
                        tangents[realIndex] = new Vector4(currLine[tangentColumnX], currLine[tangentColumnY], currLine[tangentColumnZ], currLine[tangentColumnW]);
                    }

                    if (hasColorProp)
                    {
                        vertexColors[realIndex] = new Color(currLine[colorColumnX], currLine[colorColumnY], currLine[colorColumnZ], currLine[colorColumnW]);
                    }

                    for (int ti = 0; ti < texcoordColumnID.Length; ti++)
                    {
                        if (!enableTexcoord[ti])
                            continue;
                        vertexTexcoords[ti][realIndex] = new Vector4(
                            texcoordColumnX[ti] < 0 ? float.MinValue : currLine[texcoordColumnX[ti]],
                            texcoordColumnY[ti] < 0 ? float.MinValue : currLine[texcoordColumnY[ti]],
                            texcoordColumnZ[ti] < 0 ? float.MinValue : currLine[texcoordColumnZ[ti]],
                            texcoordColumnW[ti] < 0 ? float.MinValue : currLine[texcoordColumnW[ti]]);
                    }

                }
                else
                {
                    return null;
                }
            }

            Mesh mesh = new Mesh();
            Debug.Log("vertexLength" + vertexLength);
            if (vertexLength > 65535) //需要优化 折叠顶点
            {
                //mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; //虽然可以UInt32解决但是移动端不建议
                Debug.Log("需要优化 折叠顶点 移动端UInt32索引支持有限");
                List<Vector3> listV = new List<Vector3>();
                List<Color> listColor = new List<Color>();
                List<Vector4> listTangent = new List<Vector4>();
                List<Vector3> listNormal = new List<Vector3>();
                List<Vector4>[] listTexcoords = new List<Vector4>[vertexTexcoords.Count];
                for (int i = 0; i < listTexcoords.Length; i++)
                {
                    listTexcoords[i] = new List<Vector4>();
                }
                for (int i = 0; i < outputIndexBuff.Length; i++)
                {
                    int index = outputIndexBuff[i];
                    Vector3 before = vertices[outputIndexBuff[i]];
                    bool hasSomePos = false;
                    for (int j = 0; j < listV.Count; j++)
                    {
                        if (listV[j] == before)
                        {
                            hasSomePos = true;
                            outputIndexBuff[i] = j;
                            break;
                        }
                    }
                    if (!hasSomePos)
                    {
                        outputIndexBuff[i] = listV.Count;
                        listV.Add(before);
                        for (int c = 0; c < listTexcoords.Length; c++)
                        {
                            listTexcoords[c].Add(vertexTexcoords[c][index]);
                        }
                        if (hasColorProp)
                        {
                            listColor.Add(vertexColors[index]);
                        }
                        if (hasNormalProp)
                        {
                            listNormal.Add(normals[index]);
                        }
                        if (hasTangentProp)
                        {
                            listTangent.Add(tangents[index]);
                        }
                    }
                }
                Debug.Log("折叠后 顶点数=" + listV.Count);
                vertices = listV.ToArray();
                for (int i = 0; i < vertexTexcoords.Count; i++)
                {
                    vertexTexcoords[i] = listTexcoords[i].ToArray();
                }

                if (hasColorProp)
                {
                    vertexColors = listColor.ToArray();
                }
                if (hasNormalProp)
                {
                    normals = listNormal.ToArray();
                }
                if (hasTangentProp)
                {
                    tangents = listTangent.ToArray();
                }
            }

            mesh.vertices = vertices;
            mesh.SetTriangles(outputIndexBuff, 0);

            for (int ti = 0; ti < texcoordColumnID.Length; ti++)
            {
                if (!enableTexcoord[ti])
                    continue;
                mesh.SetUVs(ti, vertexTexcoords[ti]);
            }


            if (hasNormalProp)
            {

                if (decodeNormals) //有些公司喜欢对法线加密
                {
                    for(int i=0;i< normals.Length;i++)
                    {
                        normals[i] = normals[i] * 2 - Vector3.one;
                    }
                }
                mesh.normals = normals;
            }
            else
            {
                mesh.RecalculateNormals();
            }

            if (hasNormalProp)
            {
                mesh.tangents = tangents;
            }
            else
            {
                mesh.RecalculateTangents();
            }

            if (hasColorProp)
            {
                mesh.colors = vertexColors;
            }
            if (flipNormals)
            {
                mesh.triangles = mesh.triangles.Reverse().ToArray();
                //mesh.normals = mesh.normals.Reverse().ToArray();
            }
            // if (flipNormals)
            // {
            //     mesh.triangles = mesh.triangles.Reverse().ToArray();
            // }

            return mesh;
        }

        private static void CreateMeshAssetAndShow(Mesh mesh, string filePath, string fileName)
        {
            string exportPath = Path.Combine(filePath, fileName);

            //var shader = (Shader)AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(debugShaderGUID), typeof(Shader));
            //Material material = new Material(shader);
            //Material material = (Material)AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(debugMaterialGUID), typeof(Material));

            GameObject obj = new GameObject();
            var meshFilter = obj.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            var meshRenderer = obj.AddComponent<MeshRenderer>();
            //meshRenderer.sharedMaterial = material; // Use default Lit

            obj.name = fileName.Split('.')[0] + "Mesh";
            ExportModelOptions exportModelOptions = new ExportModelOptions();
            exportModelOptions.ExportFormat = fbxExportFormat;
            ModelExporter.ExportObject(exportPath, obj, exportModelOptions);
            SaveMeshToAsset(mesh, filePath + fileName + "_fullData.mesh");

            // Clean
            GameObject.DestroyImmediate(obj);

            // Ping Object
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath(exportPath, typeof(UnityEngine.Object)));
        }

        private static void ReadAllRows(string[] allTexts, int headsLength, ref List<float[]> allRows)
        {
            foreach (var lineText in allTexts.Skip(1))
            {
                if (lineText.Length <= 10)
                {
                    continue;
                }

                var cells = lineText.Trim().Replace(" ", "").Split(',');
                if (cells.Length != headsLength)
                {
                    continue;
                }

                float[] cellData = new float[cells.Length];
                for (int i = 0; i < cells.Length; ++i)
                {
                    if (!float.TryParse(cells[i], out cellData[i]))
                    {
                        Debug.Log("Don't have csv data.");
                    }
                }

                allRows.Add(cellData);
            }
        }

        public static int GetColumnIndex(string[] input, string key)
        {
            for (int i = 0; i < input.Length; ++i)
            {
                if (input[i] == key)
                {
                    return i;
                }
            }

            return -1;
        }

        private string ConvertToRelativePath(string absolutePath)
        {
            string relativePath = absolutePath;

            if (absolutePath.StartsWith(Application.dataPath))
            {
                relativePath = "Assets" + absolutePath.Substring(Application.dataPath.Length);
            }

            return relativePath;
        }

        private static class Styles
        {
            public static readonly GUIContent csvAsset = EditorGUIUtility.TrTextContent("csv Asset");
            public static readonly GUIContent csvPAsset =EditorGUIUtility.TrTextContent("csv P Vec Asset");
            public static readonly GUIContent csvVMAsset =EditorGUIUtility.TrTextContent("csv VM Asset");
            public static readonly GUIContent matrixVP = EditorGUIUtility.TrTextContent("Matrix VP");
            public static readonly GUIContent matrixM = EditorGUIUtility.TrTextContent("Matrix M");
            public static readonly GUIContent scale = EditorGUIUtility.TrTextContent("scale");
            public static readonly GUIContent flipNormal = EditorGUIUtility.TrTextContent("flipNormal");
            public static readonly GUIContent decodeNormal = EditorGUIUtility.TrTextContent("decodeNormal");
            public static readonly GUIContent flipUV = EditorGUIUtility.TrTextContent("flipUV");
            public static readonly GUIContent enableTexcoord = EditorGUIUtility.TrTextContent("Enable");
            public static readonly GUIContent enableTexcoordUVChange = EditorGUIUtility.TrTextContent("XYChange");


            public static readonly GUIContent meshProperties = EditorGUIUtility.TrTextContent("Mesh Properties");

            public static string VTXStr = "VTX";
            public static string IDXStr = "IDX";
            public static string positionStr = "Position:";
            public static string normalStr = "Normal:";
            public static string tangentStr = "Tangent:";
            public static string colorStr = "Color:";
            public static string texcoordStr = "Texcoord";

            public static string fbxFormatStr = "FBX format:";
            public static string materialDebugMode = "MaterialDebugMode:";

            public static string matrixVPStr = "hlslcc_mtx4x4unity_MatrixVP";
            public static string matrixMStr = "hlslcc_mtx4x4unity_ObjectToWorld";
            public static string tiquStr = @"\[([^\]]+)\]";
        }


        private void HandleMouseEvents(Rect previewRect)
        {
            Event e = Event.current;
            if ((e.isMouse || e.isScrollWheel) && previewRect.Contains(e.mousePosition))
            {
                switch (e.type)
                {
                    case EventType.MouseDown:
                        if (e.button == 0) // 左键拖动旋转
                        {
                            dragStartPos = e.mousePosition;
                            e.Use();
                        }
                        else if (e.button == 2) // 中键拖动物体位置
                        {
                            dragStartPos = e.mousePosition;
                            e.Use();
                        }
                        break;

                    case EventType.MouseDrag:
                        if (e.button == 0) // 左键拖动旋转
                        {
                            Vector2 delta = e.mousePosition - dragStartPos;
                            previewDir += delta * 0.5f; // 调整旋转速度
                            dragStartPos = e.mousePosition;
                            e.Use();
                        }
                        else if (e.button == 2) // 中键拖动物体位置
                        {
                            Vector2 delta = e.mousePosition - dragStartPos;
                            objectPosition += new Vector3(delta.x * 0.01f, -delta.y * 0.01f, 0);
                            dragStartPos = e.mousePosition;
                            e.Use();
                        }
                        break;

                    case EventType.ScrollWheel: // 滚轮缩放
                        zoom += e.delta.y * 0.1f;
                        zoom = Mathf.Clamp(zoom, 1f, 10f);
                        e.Use();
                        break;
                }
            }
        }


        private void OnDisable()
        {
            if (previewRenderUtility != null)
            {
                previewRenderUtility.Cleanup();
                previewRenderUtility = null;
            }
        }

        static void SetMaterialDebugMode(Material material, MaterialDebugMode debugMode)
        {
            if (material != null)
            {
                material.DisableKeyword("_BASIC_LIGHTING");
                material.DisableKeyword("_NORMAL_DEBUG");
                material.DisableKeyword("_TANGENT_DEBUG");
                material.DisableKeyword("_VERTEX_COLOR_DEBUG");
                material.DisableKeyword("_TEXCOORD0_DEBUG");
                material.DisableKeyword("_TEXCOORD1_DEBUG");
                material.DisableKeyword("_TEXCOORD2_DEBUG");
                material.DisableKeyword("_TEXCOORD3_DEBUG");
                material.DisableKeyword("_TEXCOORD4_DEBUG");


                switch (debugMode)
                {
                    case MaterialDebugMode.BasicLighting:
                        material.EnableKeyword("_BASIC_LIGHTING");
                        break;
                    case MaterialDebugMode.Normal:
                        material.EnableKeyword("_NORMAL_DEBUG");
                        break;
                    case MaterialDebugMode.Tangent:
                        material.EnableKeyword("_TANGENT_DEBUG");
                        break;
                    case MaterialDebugMode.VertexColor:
                        material.EnableKeyword("_VERTEX_COLOR_DEBUG");
                        break;
                    case MaterialDebugMode.Texcoord0:
                        material.EnableKeyword("_TEXCOORD0_DEBUG");
                        break;
                    case MaterialDebugMode.Texcoord1:
                        material.EnableKeyword("_TEXCOORD1_DEBUG");
                        break;
                    case MaterialDebugMode.Texcoord2:
                        material.EnableKeyword("_TEXCOORD2_DEBUG");
                        break;
                    case MaterialDebugMode.Texcoord3:
                        material.EnableKeyword("_TEXCOORD3_DEBUG");
                        break;
                    case MaterialDebugMode.Texcoord4:
                        material.EnableKeyword("_TEXCOORD4_DEBUG");
                        break;
                    default:
                        break;
                }

                EditorUtility.SetDirty(material);
            }
        }

        static void SetMaterialOutPutChannel(Material material, MaterialOutputChannel outputChannel)
        {
            // Disable all output channels keywords
            material.DisableKeyword("_OUTPUT_RED");
            material.DisableKeyword("_OUTPUT_GREEN");
            material.DisableKeyword("_OUTPUT_BLUE");
            material.DisableKeyword("_OUTPUT_ALPHA");

            // Enable the specific output channels based on the mask
            if ((outputChannel & MaterialOutputChannel.Red) != 0)
                material.EnableKeyword("_OUTPUT_RED");

            if ((outputChannel & MaterialOutputChannel.Green) != 0)
                material.EnableKeyword("_OUTPUT_GREEN");

            if ((outputChannel & MaterialOutputChannel.Blue) != 0)
                material.EnableKeyword("_OUTPUT_BLUE");

            if ((outputChannel & MaterialOutputChannel.Alpha) != 0)
                material.EnableKeyword("_OUTPUT_ALPHA");

            EditorUtility.SetDirty(material);
        }

        private static void SaveMeshToAsset(Mesh mesh, string filePath)
        {
            AssetDatabase.CreateAsset(mesh, filePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
