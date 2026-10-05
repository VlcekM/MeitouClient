// The Vulkan backend of IGl. Layout:
//   VkGl*        the IGl translation (state tracking, lazy pipelines, render passes, uploads)
//   Upscalers/   FSR and DLSS (Streamline) on VkGl's images
//   VulkanPresenter  framebuffer 0 to the swapchain
// The device (Core/) and the shader compiler (Shaders/) moved to src/Meitou.Rendering/Gpu (docs/renderer-native.md, wave 2 step 1).
