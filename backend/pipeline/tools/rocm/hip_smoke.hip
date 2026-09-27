// hipcc --offload-arch=gfx1201 hip_smoke.hip -o hip_smoke && ./hip_smoke
#include <hip/hip_runtime.h>
#include <cstdio>

__global__ void saxpy(int n, float a, const float* x, float* y) {
  int i = blockIdx.x * blockDim.x + threadIdx.x;
  if (i < n) y[i] = a * x[i] + y[i];
}

#define CK(e) do { hipError_t r = (e); if (r != hipSuccess) { \
  printf("HIP error %s at line %d\n", hipGetErrorString(r), __LINE__); return 1; } } while (0)

int main() {
  const int n = 1 << 20;
  hipDeviceProp_t p; CK(hipGetDeviceProperties(&p, 0));
  printf("device: %s (%s), warpSize %d\n", p.name, p.gcnArchName, p.warpSize);
  float *x, *y;
  CK(hipMallocManaged(&x, n * sizeof(float))); CK(hipMallocManaged(&y, n * sizeof(float)));
  for (int i = 0; i < n; ++i) { x[i] = 1.0f; y[i] = float(i); }
  saxpy<<<(n + 255) / 256, 256>>>(n, 2.0f, x, y);
  CK(hipGetLastError()); CK(hipDeviceSynchronize());
  int bad = 0;
  for (int i = 0; i < n; ++i) bad += y[i] != float(i) + 2.0f;
  printf("saxpy n=%d mismatches=%d -> %s\n", n, bad, bad ? "FAIL" : "PASS");
  return bad != 0;
}
