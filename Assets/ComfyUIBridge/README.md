# Unity ComfyUI Bridge

A Unity 2019-compatible Editor tool for driving [ComfyUI](https://github.com/comfyanonymous/ComfyUI) workflows with a flexible, node-table-driven configuration system.

Open it from `Tools > ComfyUI Bridge > Generator`.

## Quick Start

1. Start ComfyUI locally (default `http://127.0.0.1:8188`).
2. In ComfyUI, enable developer options and use **Save (API Format)** to export your workflow as JSON.
3. Place the JSON in `Assets/ComfyUIBridge/Workflows/` (or anywhere in the project).
4. In the Generator window, assign the JSON via the **Workflow JSON** field.
5. Click **Auto Discover** — the tool scans the workflow and automatically creates editable configs for recognizable nodes.
6. Click **Generate**.

Generated files are imported into `Assets/ComfyUIBridge/Generated` by default.

## Supported Node Types

Each entry in the **Node Configuration** list maps to one node in the ComfyUI workflow JSON by its numeric ID.

| Config Type | ComfyUI Node(s) | What It Overrides |
|---|---|---|
| **Text Prompt** | CLIPTextEncode, SDXL text_g/text_l, String | `text`, `text_g`, `text_l`, `string` |
| **KSampler** | KSampler, KSamplerAdvanced, SamplerCustom | `seed`/`noise_seed`, `steps`, `cfg`, `denoise`, optional `sampler_name` & `scheduler` |
| **Empty Latent Image** | EmptyLatentImage | `width`, `height`, `batch_size` |
| **Empty Latent Audio** | EmptyLatentAudio | `seconds` |
| **Checkpoint / VAE** | CheckpointLoaderSimple, VAELoader, UNETLoader | `ckpt_name` / `vae_name` / `model_name` / `unet_name` |
| **LoRA Loader** | LoraLoader | `lora_name`, `strength_model`, `strength_clip` |
| **Image Input** | LoadImage | Uploads a Texture2D to ComfyUI via `/upload/image`, then sets `image` |
| **Generic Input** | Any node | Arbitrary key-value pair |

## Output Types

Set **Expected Output** to filter which files ComfyUI returns:

- **Image** — `.png`, `.jpg`, `.jpeg`, `.webp` → imported as `Texture2D`
- **Audio** — `.wav`, `.mp3`, `.ogg`, `.aif`, `.flac` → imported as `AudioClip`
- **Object3D** — `.glb`, `.gltf`, `.obj`, `.fbx` → saved as raw asset
- **All** — collects every file

All matching files are downloaded (not just the first).

## Features

- **Auto Discover**: scans the workflow JSON and auto-creates node configs with values pre-filled from the workflow.
- **Load Defaults**: per-node button to re-read the workflow JSON and refresh that node's fields.
- **class_type Validation**: warns if a config's type doesn't match the workflow node's `class_type`.
- **Field Fallback**: tries multiple input names (e.g. `text` → `text_g` → `text_l` → `string`; `seed` → `noise_seed`; `ckpt_name` → `vae_name` → `model_name`).
- **Image Upload**: uploads `Texture2D` to ComfyUI's `/upload/image` endpoint with automatic readable-texture conversion.
- **Multi-Output**: collects and imports all matching output files from a single generation.
- **Presets**: save and load the full configuration (server URL, workflow path, output folder, output type, all node configs) as a JSON preset file.
- **Persistent Settings**: server URL, output folder, workflow path, and output type are saved to `EditorPrefs`.

## Presets

- **Save Preset** — writes a JSON file to `Assets/ComfyUIBridge/Presets/` (or any path).
- **Load Preset** — restores all settings and node configs from a preset file. `Texture2D` references are restored via their AssetDatabase path.

## Compatibility

- Unity 2019+ (C# 7.3, IMGUI-based `EditorWindow`).
- No external packages required — JSON parsing uses an embedded `MiniJson` implementation.
- Works with any ComfyUI workflow exported in API Format.
