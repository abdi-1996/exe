import io
import json
import os
import uuid
import urllib.error
import urllib.request
from pathlib import Path


def _studio_request(path, payload):
    base_url = os.environ.get("AISTUDIO_INTERNAL_URL", "").rstrip("/")
    token = os.environ.get("AISTUDIO_INTERNAL_TOKEN", "")
    if not base_url or not token:
        raise RuntimeError("AI Studio backend connection is not configured. Start ComfyUI from managed engine mode.")
    request = urllib.request.Request(
        base_url + path,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json", "X-AIStudio-Token": token},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"AI Studio returned HTTP {error.code}: {detail}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"AI Studio backend is unavailable: {error.reason}") from error


class AIStudioPromptEnhancer:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "prompt": ("STRING", {"multiline": True, "dynamicPrompts": False}),
            "instruction": ("STRING", {"multiline": True, "default": "Improve the prompt while preserving its meaning."}),
        }}

    RETURN_TYPES = ("STRING", "STRING")
    RETURN_NAMES = ("enhanced_prompt", "original_prompt")
    FUNCTION = "enhance"
    CATEGORY = "AI Studio/LLM"
    DESCRIPTION = "Uses the AI provider configured in AI Studio. Credentials remain in the backend."

    def enhance(self, prompt, instruction):
        response = _studio_request("/api/assistant/chat", {
            "message": prompt,
            "useVision": False,
            "referenceIds": [],
            "systemInstruction": instruction,
        })
        text = response.get("text", "").strip()
        if not text:
            raise RuntimeError("The configured AI provider returned an empty prompt.")
        return (text, prompt)


class AIStudioReferenceVision:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "image": ("IMAGE",),
            "instruction": ("STRING", {"multiline": True, "default": "Describe the image for a generation prompt."}),
        }}

    RETURN_TYPES = ("STRING",)
    RETURN_NAMES = ("description",)
    FUNCTION = "analyze"
    CATEGORY = "AI Studio/Vision"
    DESCRIPTION = "Sends the selected image to the configured vision provider after the workflow is explicitly executed."

    def analyze(self, image, instruction):
        try:
            from PIL import Image
            import numpy as np
        except ImportError as error:
            raise RuntimeError("Pillow and NumPy are required for AI Studio vision node.") from error
        if image is None or len(image) == 0:
            raise RuntimeError("No image was supplied.")
        array = (image[0].cpu().numpy() * 255.0).clip(0, 255).astype(np.uint8)
        data_root = os.environ.get("AISTUDIO_DATA_DIR", "")
        if not data_root:
            raise RuntimeError("AISTUDIO_DATA_DIR is not configured.")
        reference_id = uuid.uuid4().hex
        target = Path(data_root) / "references" / f"{reference_id}.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(array).save(target, format="PNG")
        response = _studio_request("/api/assistant/chat", {
            "message": instruction,
            "useVision": True,
            "referenceIds": [reference_id],
        })
        text = response.get("text", "").strip()
        if not text:
            raise RuntimeError("The configured vision provider returned an empty description.")
        return (text,)


class AIStudioSceneDescription:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "subject": ("STRING", {"multiline": True}),
            "action": ("STRING", {"multiline": True}),
            "camera": ("STRING", {"default": "medium shot"}),
            "lighting": ("STRING", {"default": "cinematic lighting"}),
            "atmosphere": ("STRING", {"default": ""}),
        }}

    RETURN_TYPES = ("STRING", "STRING")
    RETURN_NAMES = ("description", "scene_json")
    FUNCTION = "describe"
    CATEGORY = "AI Studio/Structure"

    def describe(self, subject, action, camera, lighting, atmosphere):
        scene = {"subject": subject, "action": action, "camera": camera, "lighting": lighting, "atmosphere": atmosphere}
        description = ", ".join(value.strip() for value in scene.values() if value and value.strip())
        return (description, json.dumps(scene, ensure_ascii=False))
