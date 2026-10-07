"""AI Studio nodes for a managed ComfyUI instance.

The LLM and vision nodes call the local AI Studio backend. Provider credentials stay
in the Windows process and are never stored in a workflow or returned to ComfyUI.
"""

from .nodes import AIStudioPromptEnhancer, AIStudioReferenceVision, AIStudioSceneDescription

NODE_CLASS_MAPPINGS = {
    "AIStudioPromptEnhancer": AIStudioPromptEnhancer,
    "AIStudioReferenceVision": AIStudioReferenceVision,
    "AIStudioSceneDescription": AIStudioSceneDescription,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "AIStudioPromptEnhancer": "AI Studio · Prompt Enhancer",
    "AIStudioReferenceVision": "AI Studio · Reference Vision",
    "AIStudioSceneDescription": "AI Studio · Scene Description",
}

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]

