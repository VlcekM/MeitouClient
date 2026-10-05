// The Vulkan backend. Layout:
//   Core/     instance, device, queues, memory suballocation, frames in flight, deferred deletion, pipeline cache
//   Shaders/  GLSL to SPIR-V (shaderc, relaxed Vulkan rules), interface location matching, SPIR-V reflection, disk cache
//   VkGl*     the IGl translation (state tracking, lazy pipelines, render passes, uploads)
