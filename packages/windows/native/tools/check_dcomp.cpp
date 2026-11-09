// Checks fire_display_dcomp.h against the Windows SDK (the headers of MinGW): the places of the COM methods it calls, the GUIDs and the structures it writes out.
// Built with x86_64-w64-mingw32-g++ and run under Wine (or Windows) by tools/check_dcomp.sh and the native tests; prints "dcomp ok" or what differs.
// (MinGW's g++ lays vtables out in declaration order; dcomp.h orders the overloads of IDCompositionVisual as MSVC's binary does - so a pointer to a member function
// tells the place: Itanium ABI, 1 + offset for a virtual one.)
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include "../fire_display_c.h"
#include "../fire_display_dcomp.h"

template <typename T> static long slotOf(T p) { long long v; memcpy(&v, &p, sizeof v); return (long)((v - 1) / (long long)sizeof(void*)); }
static int bad = 0;
static void slot(const char* name, long real, long ours) { if (real != ours) { printf("%s: the SDK has %ld, fire_display_dcomp.h %ld\n", name, real, ours); bad = 1; } }
static void guid(const char* name, const GUID& real, const FireDcGuid& ours) { if (memcmp(&real, &ours, sizeof ours) != 0) { printf("%s: another GUID\n", name); bad = 1; } }
#define SLOT(m, e) slot(#m, slotOf(m), e)

static_assert(sizeof(FireDcTexDesc) == sizeof(D3D11_TEXTURE2D_DESC), "D3D11_TEXTURE2D_DESC");
static_assert(offsetof(FireDcTexDesc, miscFlags) == offsetof(D3D11_TEXTURE2D_DESC, MiscFlags), "D3D11_TEXTURE2D_DESC.MiscFlags");
static_assert(sizeof(FireDcSwapDesc) == sizeof(DXGI_SWAP_CHAIN_DESC1), "DXGI_SWAP_CHAIN_DESC1");
static_assert(offsetof(FireDcSwapDesc, alphaMode) == offsetof(DXGI_SWAP_CHAIN_DESC1, AlphaMode), "DXGI_SWAP_CHAIN_DESC1.AlphaMode");
static_assert(sizeof(FireDcBox) == sizeof(D3D11_BOX), "D3D11_BOX");
static_assert(sizeof(FireDcQueryDesc) == sizeof(D3D11_QUERY_DESC), "D3D11_QUERY_DESC");
static_assert(sizeof(FireDcMatrix) == sizeof(D2D_MATRIX_3X2_F), "D2D_MATRIX_3X2_F");
static_assert(FIRE_DC_DESC_LUID == offsetof(DXGI_ADAPTER_DESC, AdapterLuid) && sizeof(DXGI_ADAPTER_DESC) == 304, "DXGI_ADAPTER_DESC");
static_assert(FIRE_DC_RGBA == DXGI_FORMAT_R8G8B8A8_UNORM && FIRE_DC_ALPHA_PREMULTIPLIED == DXGI_ALPHA_MODE_PREMULTIPLIED && FIRE_DC_ALPHA_IGNORE == DXGI_ALPHA_MODE_IGNORE
              && FIRE_DC_FLIP_SEQUENTIAL == DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL && FIRE_DC_RENDER_TARGET_OUTPUT == DXGI_USAGE_RENDER_TARGET_OUTPUT, "constants");

int main() {
    SLOT(&ID3D11Device::CreateTexture2D, FIRE_DC_DEV_CREATE_TEXTURE2D);
    SLOT(&ID3D11Device::CreateQuery, FIRE_DC_DEV_CREATE_QUERY);
    SLOT(&ID3D11DeviceContext::End, FIRE_DC_CTX_END);
    SLOT(&ID3D11DeviceContext::GetData, FIRE_DC_CTX_GET_DATA);
    SLOT(&ID3D11DeviceContext::CopyResource, FIRE_DC_CTX_COPY_RESOURCE);
    SLOT(&ID3D11DeviceContext::UpdateSubresource, FIRE_DC_CTX_UPDATE_SUBRESOURCE);
    SLOT(&ID3D11DeviceContext::Flush, FIRE_DC_CTX_FLUSH);
    SLOT(&IDXGIDevice::GetAdapter, FIRE_DC_DXGIDEV_GET_ADAPTER);
    SLOT(&IDXGIObject::GetParent, FIRE_DC_OBJ_GET_PARENT);
    SLOT(&IDXGIAdapter::GetDesc, FIRE_DC_ADAPTER_GET_DESC);
    SLOT(&IDXGIResource::GetSharedHandle, FIRE_DC_RES_GET_SHARED_HANDLE);
    SLOT(&IDXGISwapChain::Present, FIRE_DC_CHAIN_PRESENT);
    SLOT(&IDXGISwapChain::GetBuffer, FIRE_DC_CHAIN_GET_BUFFER);
    SLOT(&IDXGISwapChain::ResizeBuffers, FIRE_DC_CHAIN_RESIZE_BUFFERS);
    SLOT(&IDXGIFactory2::CreateSwapChainForComposition, FIRE_DC_FACTORY2_FOR_COMPOSITION);
    SLOT(&IDCompositionDevice::Commit, FIRE_DC_COMP_COMMIT);
    SLOT(&IDCompositionDevice::CreateTargetForHwnd, FIRE_DC_COMP_TARGET_FOR_HWND);
    SLOT(&IDCompositionDevice::CreateVisual, FIRE_DC_COMP_CREATE_VISUAL);
    SLOT(&IDCompositionTarget::SetRoot, FIRE_DC_TARGET_SET_ROOT);
    SLOT(&IDCompositionVisual::SetContent, FIRE_DC_VISUAL_SET_CONTENT);
    SLOT(&IDCompositionVisual::SetBitmapInterpolationMode, FIRE_DC_VISUAL_SET_INTERPOLATION);
    slot("IDCompositionVisual::SetTransform(const D2D_MATRIX_3X2_F&)",
         slotOf(static_cast<HRESULT (STDMETHODCALLTYPE IDCompositionVisual::*)(const D2D_MATRIX_3X2_F&)>(&IDCompositionVisual::SetTransform)), FIRE_DC_VISUAL_SET_TRANSFORM_MATRIX);
    guid("IDXGIDevice", __uuidof(IDXGIDevice), FIRE_IID_IDXGIDevice);
    guid("IDXGIFactory2", __uuidof(IDXGIFactory2), FIRE_IID_IDXGIFactory2);
    guid("ID3D11Texture2D", __uuidof(ID3D11Texture2D), FIRE_IID_ID3D11Texture2D);
    guid("IDXGIResource", __uuidof(IDXGIResource), FIRE_IID_IDXGIResource);
    guid("IDCompositionDevice", __uuidof(IDCompositionDevice), FIRE_IID_IDCompositionDevice);
    printf(bad ? "dcomp differs\n" : "dcomp ok\n");
    return bad;
}
