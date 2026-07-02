# Unity ComfyUI Bridge

This folder contains a Unity 2019-compatible Editor tool based on the workflow approach from `Amin-HP/Unity-ComfyUI-Bridge`.

Open it from `Tools > ComfyUI Bridge > Generator`.

Default settings expect ComfyUI at `http://127.0.0.1:8188` and a text-to-image API workflow at:

`Assets/ComfyUIBridge/Workflows/text2image.json`

In ComfyUI, enable developer options and use "Save (API Format)" for custom workflows. Then assign that JSON file in the window and update the node IDs for prompt, negative prompt, sampler, latent size, and checkpoint.

Generated images are imported into:

`Assets/ComfyUIBridge/Generated`
