#include "ipc.hpp"
#include "openvr.h"
#include <DirectXMath.h>
#include <array>
#include <cmath>
#include <cstring>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <tlhelp32.h>
#include <vector>
#include <windows.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;
using namespace DirectX;
namespace
{

constexpr char GeometryShader[] = R"(
cbuffer Transform : register(b0) { row_major float4x4 worldViewProjection; };
struct Vertex { float3 position : POSITION; float3 color : COLOR; };
struct Pixel { float4 position : SV_POSITION; float3 color : COLOR; };
Pixel VS(Vertex v) { Pixel p; p.position = mul(float4(v.position,1),worldViewProjection); p.color=v.color; return p; }
float4 PS(Pixel p) : SV_TARGET { return float4(p.color,1); }
)";
constexpr char MirrorShader[] = R"(
Texture2D image : register(t0); SamplerState sampleImage : register(s0);
struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD; };
Pixel VS(uint id : SV_VertexID) {
  float2 uv = float2((id << 1) & 2, id & 2);
  Pixel p; p.position=float4(uv.x*2-1,1-uv.y*2,0,1); p.uv=uv; return p;
}
float4 PS(Pixel p) : SV_TARGET { return image.Sample(sampleImage,p.uv); }
)";

void Check(HRESULT result, const char *operation)
{
    if (SUCCEEDED(result))
        return;
    std::ostringstream message;
    message << operation << " failed (HRESULT 0x" << std::hex << static_cast<unsigned long>(result)
            << ')';
    throw std::runtime_error(message.str());
}

ComPtr<ID3DBlob> Compile(const char *source, const char *entry, const char *target)
{
    ComPtr<ID3DBlob> bytecode, errors;
    const auto result = D3DCompile(
        source, std::strlen(source), "original-test-scene", nullptr, nullptr, entry, target,
        D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_WARNINGS_ARE_ERRORS, 0, &bytecode, &errors);
    if (FAILED(result))
        throw std::runtime_error(errors ? static_cast<const char *>(errors->GetBufferPointer())
                                        : "Shader compilation failed");
    return bytecode;
}

ComPtr<IDXGIFactory1> CreateGraphicsFactory()
{
    ComPtr<IDXGIFactory1> factory;
    // Valve's Submit documentation requires DXGI 1.1+ initialization before the
    // D3D device. Legacy CreateDXGIFactory causes compositor error 106.
    Check(CreateDXGIFactory1(__uuidof(IDXGIFactory1),
                              reinterpret_cast<void **>(factory.GetAddressOf())),
          "CreateDXGIFactory1");
    return factory;
}

D3D11_TEXTURE2D_DESC EyeTextureDescription(UINT width, UINT height)
{
    D3D11_TEXTURE2D_DESC texture{};
    texture.Width = width;
    texture.Height = height;
    texture.MipLevels = texture.ArraySize = 1;
    texture.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    texture.SampleDesc.Count = 1;
    texture.Usage = D3D11_USAGE_DEFAULT;
    texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    // Submit receives a normal ID3D11Texture2D. The compositor owns its sharing
    // path; no shared-handle or keyed-mutex submission is implied here.
    return texture;
}

// OpenVR poses use column-vector transforms. DirectXMath uses row vectors.
// Transposition converts the representation; no handedness/floor transform is added.
XMMATRIX PoseMatrix(const vr::HmdMatrix34_t &p)
{
    return XMMatrixSet(p.m[0][0], p.m[1][0], p.m[2][0], 0, p.m[0][1], p.m[1][1], p.m[2][1], 0,
                       p.m[0][2], p.m[1][2], p.m[2][2], 0, p.m[0][3], p.m[1][3], p.m[2][3], 1);
}
XMMATRIX ProjectionMatrix(const vr::HmdMatrix44_t &p)
{
    return XMMatrixSet(p.m[0][0], p.m[1][0], p.m[2][0], p.m[3][0], p.m[0][1], p.m[1][1], p.m[2][1],
                       p.m[3][1], p.m[0][2], p.m[1][2], p.m[2][2], p.m[3][2], p.m[0][3], p.m[1][3],
                       p.m[2][3], p.m[3][3]);
}

bool RuntimeActive()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
        return false;
    PROCESSENTRY32W process{};
    process.dwSize = sizeof(process);
    bool active = false;
    if (Process32FirstW(snapshot, &process))
        do
        {
            if (_wcsicmp(process.szExeFile, L"vrserver.exe") == 0)
            {
                active = true;
                break;
            }
        } while (Process32NextW(snapshot, &process));
    CloseHandle(snapshot);
    return active;
}

std::filesystem::path DefaultLogPath()
{
    wchar_t local[MAX_PATH]{};
    const auto count = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    if (count == 0 || count >= MAX_PATH)
        throw std::runtime_error("LOCALAPPDATA is unavailable");
    SYSTEMTIME now{};
    GetSystemTime(&now);
    std::wostringstream filename;
    filename << L"scene-" << now.wYear << '-' << std::setw(2) << std::setfill(L'0') << now.wMonth
             << '-' << std::setw(2) << now.wDay << '-' << std::setw(2) << now.wHour << std::setw(2)
             << now.wMinute << std::setw(2) << now.wSecond << '-' << GetCurrentProcessId()
             << L".jsonl";
    return std::filesystem::path(local) / L"VRC-SWITCHEROONIE" / L"test-scene" / filename.str();
}

struct Vertex
{
    XMFLOAT3 position, color;
};
struct JsonNumber
{
    double value;
};
std::ostream &operator<<(std::ostream &stream, JsonNumber number)
{
    if (std::isfinite(number.value))
        return stream << number.value;
    return stream << "null";
}

bool SyntheticHeadAvailable(const sw::Status &status, bool statusAlive, int64_t now,
                             int64_t frequency, double &ageMilliseconds)
{
    ageMilliseconds = status.syntheticHeadQpc > 0 && frequency > 0
                          ? (static_cast<double>(now) - status.syntheticHeadQpc) * 1000 / frequency
                          : -1;
    return statusAlive && (status.reserved0 & 1) != 0 && status.actualMode == 1 &&
           status.syntheticHeadValid != 0 && status.syntheticHeadEpoch == status.ackEpoch &&
           status.anchorEpoch == status.ackEpoch &&
           ageMilliseconds >= 0 && ageMilliseconds <= sw::WatchdogMilliseconds;
}
void Line(std::vector<Vertex> &vertices, XMFLOAT3 a, XMFLOAT3 b, XMFLOAT3 color)
{
    vertices.push_back({a, color});
    vertices.push_back({b, color});
}
std::vector<Vertex> GridVertices()
{
    std::vector<Vertex> vertices;
    for (int i = -10; i <= 10; ++i)
    {
        const float n = i * 0.5f;
        Line(vertices, {n, 0, -5}, {n, 0, 5}, {0.15f, 0.25f, 0.30f});
        Line(vertices, {-5, 0, n}, {5, 0, n}, {0.15f, 0.25f, 0.30f});
    }
    Line(vertices, {0, 0, 0}, {1, 0, 0}, {1, 0.2f, 0.2f});
    Line(vertices, {0, 0, 0}, {0, 1, 0}, {0.2f, 1, 0.2f});
    Line(vertices, {0, 0, 0}, {0, 0, -1}, {0.2f, 0.4f, 1});
    return vertices;
}
std::vector<Vertex> CubeVertices()
{
    constexpr XMFLOAT3 corners[] = {{-.5f, -.5f, -.5f}, {.5f, -.5f, -.5f}, {.5f, .5f, -.5f},
                                    {-.5f, .5f, -.5f},  {-.5f, -.5f, .5f}, {.5f, -.5f, .5f},
                                    {.5f, .5f, .5f},    {-.5f, .5f, .5f}};
    constexpr int faces[6][6] = {{0, 1, 2, 0, 2, 3}, {5, 4, 7, 5, 7, 6}, {4, 0, 3, 4, 3, 7},
                                 {1, 5, 6, 1, 6, 2}, {3, 2, 6, 3, 6, 7}, {4, 5, 1, 4, 1, 0}};
    constexpr XMFLOAT3 colors[] = {{.25f, .75f, .85f}, {.25f, .5f, .6f}, {.8f, .35f, .35f},
                                   {.9f, .7f, .25f},   {.3f, .9f, .55f}, {.25f, .35f, .3f}};
    std::vector<Vertex> vertices;
    for (int face = 0; face < 6; ++face)
        for (const auto corner : faces[face])
            vertices.push_back({corners[corner], colors[face]});
    return vertices;
}

class Scene
{
  public:
    ~Scene()
    {
        if (context_)
        {
            context_->ClearState();
            context_->Flush();
        }
        if (window_ && IsWindow(window_))
            DestroyWindow(window_);
        if (system_)
            vr::VR_Shutdown();
    }
    void Run(const std::filesystem::path &logPath)
    {
        // --run is explicit authorization to own a controlled scene. It is never
        // executed by an automated test, build, panel, or installer.
        if (!RuntimeActive())
            throw std::runtime_error(
                "SteamVR is not already running; no OpenVR initialization was attempted");
        vr::EVRInitError error{};
        system_ = vr::VR_Init(&error, vr::VRApplication_Scene);
        if (error != vr::VRInitError_None || !system_)
            throw std::runtime_error(vr::VR_GetVRInitErrorAsEnglishDescription(error));
        compositor_ = vr::VRCompositor();
        if (!compositor_)
            throw std::runtime_error("The running runtime did not expose a compositor");
        compositor_->SetTrackingSpace(vr::TrackingUniverseStanding);
        system_->GetRecommendedRenderTargetSize(&eyeWidth_, &eyeHeight_);
        if (!eyeWidth_ || !eyeHeight_ || eyeWidth_ > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
            eyeHeight_ > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION)
            throw std::runtime_error("Runtime supplied an invalid recommended eye target size");
        CreateWindowAndDevice();
        CreateResources();
        const auto parent = logPath.parent_path();
        if (!parent.empty())
            std::filesystem::create_directories(parent);
        log_.open(logPath, std::ios::binary);
        if (!log_)
            throw std::runtime_error("Cannot create controlled-scene log");
        log_ << std::setprecision(9);
        log_ << "{\"event\":\"start\",\"schemaVersion\":1,\"evidence\":\"controlled-scene-live-"
                "render\",\"processId\":"
             << GetCurrentProcessId() << ",\"qpcFrequency\":" << sw::QpcFrequency()
             << ",\"eyeWidth\":" << eyeWidth_ << ",\"eyeHeight\":" << eyeHeight_
             << ",\"dxgiFactory\":\"CreateDXGIFactory1\",\"deviceFeatureLevel\":" << featureLevel_
             << ",\"eyeFormat\":\"R8G8B8A8_UNORM\",\"eyeMipLevels\":1,\"eyeSamples\":1,\"eyeMiscFlags\":0"
             << ",\"trackingUniverse\":\"standing\",\"runtimeAlreadyActive\":true,\"source\":"
                "\"WaitGetPoses post-routing\",\"driverCapturedPoseSpace\":\"pre-output driver "
                "world; standing/chaperone transform may differ\",\"routedSyntheticPoseSpace\":\"world "
                "as submitted by harness; standing/chaperone transform may differ\",\"noColdStartProof\":true}\n";
        ShowWindow(window_, SW_SHOWNORMAL);
        start_ = sw::QpcNow();
        lastLog_ = start_;
        MSG message{};
        while (!quit_)
        {
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                if (message.message == WM_QUIT)
                {
                    quit_ = true;
                    break;
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            if (quit_)
                break;
            vr::VREvent_t event{};
            while (system_->PollNextEvent(&event, sizeof(event)))
            {
                log_ << "{\"event\":\"runtime-device-event\",\"frame\":" << frame_
                     << ",\"eventType\":" << event.eventType
                     << ",\"deviceIndex\":" << event.trackedDeviceIndex << "}\n";
                if (event.eventType == vr::VREvent_Quit)
                {
                    system_->AcknowledgeQuit_Exiting();
                    quit_ = true;
                }
            }
            if (quit_)
                break;
            RenderFrame();
        }
        log_ << "{\"event\":\"stop\",\"processId\":" << GetCurrentProcessId()
             << ",\"frames\":" << frame_ << ",\"elapsedSeconds\":" << Elapsed() << "}\n";
        log_.flush();
        std::cout << "Controlled scene closed. No other process was stopped or restarted.\n";
    }

  private:
    static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto *scene = reinterpret_cast<Scene *>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            scene = static_cast<Scene *>(reinterpret_cast<CREATESTRUCTW *>(lParam)->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(scene));
        }
        if (scene && (message == WM_CLOSE || (message == WM_KEYDOWN && wParam == VK_ESCAPE)))
        {
            scene->quit_ = true;
            return 0;
        }
        if (scene && message == WM_SIZE)
        {
            scene->resize_ = true;
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    void CreateWindowAndDevice()
    {
        WNDCLASSW type{};
        type.lpfnWndProc = WindowProcedure;
        type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"SwitcheroonieControlledScene";
        type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
            throw std::runtime_error("Cannot register test window");
        RECT rectangle{0, 0, 1000, 600};
        AdjustWindowRect(&rectangle, WS_OVERLAPPEDWINDOW, FALSE);
        window_ = CreateWindowExW(
            0, type.lpszClassName,
            L"SWITCHEROONIE CONTROLLED TEST | left eye / right eye | Esc closes only this test",
            WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, rectangle.right - rectangle.left,
            rectangle.bottom - rectangle.top, nullptr, nullptr, type.hInstance, this);
        if (!window_)
            throw std::runtime_error("Cannot create test window");
        const auto factory = CreateGraphicsFactory();
        uint64_t runtimeLuid{};
        system_->GetOutputDevice(&runtimeLuid, vr::TextureType_DirectX);
        ComPtr<IDXGIAdapter> adapter;
        if (runtimeLuid)
        {
            for (UINT index = 0;; ++index)
            {
                ComPtr<IDXGIAdapter> candidate;
                const auto result = factory->EnumAdapters(index, &candidate);
                if (result == DXGI_ERROR_NOT_FOUND)
                    break;
                Check(result, "EnumAdapters");
                DXGI_ADAPTER_DESC description{};
                Check(candidate->GetDesc(&description), "GetDesc");
                uint64_t candidateLuid{};
                std::memcpy(&candidateLuid, &description.AdapterLuid, sizeof(candidateLuid));
                if (candidateLuid == runtimeLuid)
                {
                    adapter = candidate;
                    break;
                }
            }
        }
        else
        {
            int32_t adapterIndex = -1;
            system_->GetDXGIOutputInfo(&adapterIndex);
            if (adapterIndex >= 0)
                Check(factory->EnumAdapters(static_cast<UINT>(adapterIndex), &adapter),
                      "Runtime adapter selection");
        }
        if (!adapter)
            throw std::runtime_error(
                "Cannot identify the runtime's DXGI adapter; refusing a guessed GPU");
        DXGI_SWAP_CHAIN_DESC swap{};
        swap.BufferCount = 2;
        swap.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swap.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swap.OutputWindow = window_;
        swap.SampleDesc.Count = 1;
        swap.Windowed = TRUE;
        swap.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        D3D_FEATURE_LEVEL feature{};
        constexpr D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        auto result = D3D11CreateDeviceAndSwapChain(
            adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2, D3D11_SDK_VERSION, &swap, &swapChain_, &device_, &feature, &context_);
        if (result == E_INVALIDARG)
            result = D3D11CreateDeviceAndSwapChain(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels + 1, 1,
                                                   D3D11_SDK_VERSION, &swap, &swapChain_, &device_,
                                                   &feature, &context_);
        Check(result, "D3D11CreateDeviceAndSwapChain");
        featureLevel_ = static_cast<uint32_t>(feature);
        Check(factory->MakeWindowAssociation(window_, DXGI_MWA_NO_ALT_ENTER),
              "MakeWindowAssociation");
        CreateMirrorTarget();
    }

    void CreateMirrorTarget()
    {
        mirrorTarget_.Reset();
        ComPtr<ID3D11Texture2D> buffer;
        Check(swapChain_->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                    reinterpret_cast<void **>(buffer.GetAddressOf())),
              "Mirror backbuffer");
        Check(device_->CreateRenderTargetView(buffer.Get(), nullptr, &mirrorTarget_),
              "Mirror render target");
        resize_ = false;
    }
    ComPtr<ID3D11Buffer> VertexBuffer(const std::vector<Vertex> &vertices)
    {
        D3D11_BUFFER_DESC description{};
        description.ByteWidth = static_cast<UINT>(vertices.size() * sizeof(Vertex));
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA data{};
        data.pSysMem = vertices.data();
        ComPtr<ID3D11Buffer> buffer;
        Check(device_->CreateBuffer(&description, &data, &buffer), "Vertex buffer");
        return buffer;
    }
    void CreateResources()
    {
        const auto vs = Compile(GeometryShader, "VS", "vs_5_0"),
                   ps = Compile(GeometryShader, "PS", "ps_5_0");
        Check(device_->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr,
                                          &geometryVs_),
              "Geometry vertex shader");
        Check(device_->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr,
                                         &geometryPs_),
              "Geometry pixel shader");
        constexpr D3D11_INPUT_ELEMENT_DESC layout[] = {
            {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"COLOR", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0}};
        Check(device_->CreateInputLayout(layout, 2, vs->GetBufferPointer(), vs->GetBufferSize(),
                                         &layout_),
              "Geometry layout");
        const auto mirrorVs = Compile(MirrorShader, "VS", "vs_5_0"),
                   mirrorPs = Compile(MirrorShader, "PS", "ps_5_0");
        Check(device_->CreateVertexShader(mirrorVs->GetBufferPointer(), mirrorVs->GetBufferSize(),
                                          nullptr, &mirrorVs_),
              "Mirror vertex shader");
        Check(device_->CreatePixelShader(mirrorPs->GetBufferPointer(), mirrorPs->GetBufferSize(),
                                         nullptr, &mirrorPs_),
              "Mirror pixel shader");
        D3D11_BUFFER_DESC constant{};
        constant.ByteWidth = sizeof(XMFLOAT4X4);
        constant.Usage = D3D11_USAGE_DEFAULT;
        constant.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        Check(device_->CreateBuffer(&constant, nullptr, &transform_), "Transform buffer");
        auto grid = GridVertices();
        gridCount_ = static_cast<UINT>(grid.size());
        grid_ = VertexBuffer(grid);
        cube_ = VertexBuffer(CubeVertices());
        D3D11_BUFFER_DESC lines{};
        lines.ByteWidth = sizeof(Vertex) * 8 * vr::k_unMaxTrackedDeviceCount;
        lines.Usage = D3D11_USAGE_DYNAMIC;
        lines.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        lines.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        Check(device_->CreateBuffer(&lines, nullptr, &controllerLines_), "Controller lines buffer");
        D3D11_RASTERIZER_DESC raster{};
        raster.FillMode = D3D11_FILL_SOLID;
        raster.CullMode = D3D11_CULL_NONE;
        raster.DepthClipEnable = TRUE;
        Check(device_->CreateRasterizerState(&raster, &rasterizer_), "Rasterizer state");
        D3D11_DEPTH_STENCIL_DESC depth{};
        depth.DepthEnable = TRUE;
        depth.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
        depth.DepthFunc = D3D11_COMPARISON_LESS;
        Check(device_->CreateDepthStencilState(&depth, &depthState_), "Depth state");
        depth.DepthEnable = FALSE;
        Check(device_->CreateDepthStencilState(&depth, &mirrorDepthState_), "Mirror depth state");
        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        Check(device_->CreateSamplerState(&sampler, &sampler_), "Mirror sampler");
        for (int eye = 0; eye < 2; ++eye)
        {
            auto texture = EyeTextureDescription(eyeWidth_, eyeHeight_);
            Check(device_->CreateTexture2D(&texture, nullptr, &eyes_[eye].texture), "Eye texture");
            Check(device_->CreateRenderTargetView(eyes_[eye].texture.Get(), nullptr,
                                                  &eyes_[eye].target),
                  "Eye render target");
            Check(device_->CreateShaderResourceView(eyes_[eye].texture.Get(), nullptr,
                                                    &eyes_[eye].view),
                  "Eye shader view");
            texture.Format = DXGI_FORMAT_D32_FLOAT;
            texture.BindFlags = D3D11_BIND_DEPTH_STENCIL;
            ComPtr<ID3D11Texture2D> depthTexture;
            Check(device_->CreateTexture2D(&texture, nullptr, &depthTexture), "Eye depth texture");
            Check(device_->CreateDepthStencilView(depthTexture.Get(), nullptr, &eyes_[eye].depth),
                  "Eye depth target");
        }
    }

    double Elapsed() const
    {
        return static_cast<double>(sw::QpcNow() - start_) / sw::QpcFrequency();
    }
    void Draw(ID3D11Buffer *buffer, UINT count, D3D11_PRIMITIVE_TOPOLOGY topology,
              FXMMATRIX worldViewProjection)
    {
        XMFLOAT4X4 matrix;
        XMStoreFloat4x4(&matrix, worldViewProjection);
        context_->UpdateSubresource(transform_.Get(), 0, nullptr, &matrix, 0, 0);
        constexpr UINT stride = sizeof(Vertex), offset = 0;
        context_->IASetVertexBuffers(0, 1, &buffer, &stride, &offset);
        context_->IASetPrimitiveTopology(topology);
        context_->Draw(count, 0);
    }
    void UpdateControllerLines()
    {
        std::vector<Vertex> lines;
        for (uint32_t index = 1; index < vr::k_unMaxTrackedDeviceCount; ++index)
        {
            if (system_->GetTrackedDeviceClass(index) != vr::TrackedDeviceClass_Controller ||
                !poses_[index].bPoseIsValid)
                continue;
            const auto matrix = PoseMatrix(poses_[index].mDeviceToAbsoluteTracking);
            auto point = [&](float x, float y, float z) {
                XMFLOAT3 out;
                XMStoreFloat3(&out, XMVector3TransformCoord(XMVectorSet(x, y, z, 1), matrix));
                return out;
            };
            const auto origin = point(0, 0, 0);
            Line(lines, origin, point(.15f, 0, 0), {1, .2f, .2f});
            Line(lines, origin, point(0, .15f, 0), {.2f, 1, .2f});
            Line(lines, origin, point(0, 0, .15f), {.2f, .4f, 1});
            Line(lines, origin, point(0, 0, -2), {1, .95f, .45f});
        }
        controllerLineCount_ = static_cast<UINT>(lines.size());
        if (!lines.empty())
        {
            D3D11_MAPPED_SUBRESOURCE memory{};
            Check(context_->Map(controllerLines_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &memory),
                  "Controller line update");
            std::memcpy(memory.pData, lines.data(), lines.size() * sizeof(Vertex));
            context_->Unmap(controllerLines_.Get(), 0);
        }
    }
    void RenderFrame()
    {
        const auto waitError = compositor_->WaitGetPoses(
            poses_.data(), static_cast<uint32_t>(poses_.size()), nullptr, 0);
        if (waitError != vr::VRCompositorError_None)
        {
            log_ << "{\"event\":\"wait-error\",\"frame\":" << frame_
                 << ",\"error\":" << static_cast<int>(waitError) << "}\n";
            // A transient source failure is visible in the log. Never reuse an old
            // valid pose; an identity view is rendered only while marked invalid.
            poses_.fill({});
            if (!RuntimeActive())
            {
                quit_ = true;
                return;
            }
        }
        ++frame_;
        UpdateControllerLines();
        ID3D11ShaderResourceView *empty = nullptr;
        context_->PSSetShaderResources(0, 1, &empty);
        context_->IASetInputLayout(layout_.Get());
        context_->VSSetShader(geometryVs_.Get(), nullptr, 0);
        context_->PSSetShader(geometryPs_.Get(), nullptr, 0);
        context_->VSSetConstantBuffers(0, 1, transform_.GetAddressOf());
        context_->RSSetState(rasterizer_.Get());
        context_->OMSetDepthStencilState(depthState_.Get(), 0);
        const auto headView =
            poses_[0].bPoseIsValid
                ? XMMatrixInverse(nullptr, PoseMatrix(poses_[0].mDeviceToAbsoluteTracking))
                : XMMatrixIdentity();
        for (int eye = 0; eye < 2; ++eye)
        {
            const auto vrEye = static_cast<vr::EVREye>(eye);
            const auto view =
                headView *
                XMMatrixInverse(nullptr, PoseMatrix(system_->GetEyeToHeadTransform(vrEye)));
            const auto projection =
                ProjectionMatrix(system_->GetProjectionMatrix(vrEye, .05f, 30.0f));
            const float clear[] = {.025f, .04f, .06f, 1};
            context_->ClearRenderTargetView(eyes_[eye].target.Get(), clear);
            context_->ClearDepthStencilView(eyes_[eye].depth.Get(), D3D11_CLEAR_DEPTH, 1, 0);
            context_->OMSetRenderTargets(1, eyes_[eye].target.GetAddressOf(),
                                         eyes_[eye].depth.Get());
            const D3D11_VIEWPORT viewport{
                0, 0, static_cast<float>(eyeWidth_), static_cast<float>(eyeHeight_), 0, 1};
            context_->RSSetViewports(1, &viewport);
            Draw(grid_.Get(), gridCount_, D3D11_PRIMITIVE_TOPOLOGY_LINELIST, view * projection);
            Draw(cube_.Get(), 36, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
                 XMMatrixScaling(.4f, .4f, .4f) * XMMatrixTranslation(0, 1.3f, -2) * view *
                     projection);
            Draw(cube_.Get(), 36, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
                 XMMatrixScaling(.3f, .3f, .3f) * XMMatrixTranslation(-1, 1.0f, -3) * view *
                     projection);
            Draw(cube_.Get(), 36, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
                 XMMatrixScaling(.3f, .3f, .3f) * XMMatrixTranslation(1, 1.0f, -3) * view *
                     projection);
            Draw(cube_.Get(), 36, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
                 XMMatrixScaling(.15f, .15f, .15f) *
                     XMMatrixRotationY(static_cast<float>(Elapsed())) *
                     XMMatrixTranslation(0, 1.9f, -2) * view * projection);
            if (controllerLineCount_)
                Draw(controllerLines_.Get(), controllerLineCount_,
                     D3D11_PRIMITIVE_TOPOLOGY_LINELIST, view * projection);
        }
        context_->OMSetRenderTargets(0, nullptr, nullptr);
        const vr::Texture_t left{eyes_[0].texture.Get(), vr::TextureType_DirectX,
                                 vr::ColorSpace_Gamma};
        const vr::Texture_t right{eyes_[1].texture.Get(), vr::TextureType_DirectX,
                                  vr::ColorSpace_Gamma};
        const auto leftError = compositor_->Submit(vr::Eye_Left, &left),
                   rightError = compositor_->Submit(vr::Eye_Right, &right);
        RenderMirror();
        context_->Flush();
        compositor_->PostPresentHandoff();
        const auto now = sw::QpcNow();
        if (now - lastLog_ >= sw::QpcFrequency() / 10)
        {
            LogFrame(leftError, rightError);
            lastLog_ = now;
        }
    }

    void RenderMirror()
    {
        RECT client{};
        GetClientRect(window_, &client);
        const LONG width = client.right, height = client.bottom;
        if (width <= 0 || height <= 0)
            return;
        if (resize_)
        {
            context_->OMSetRenderTargets(0, nullptr, nullptr);
            mirrorTarget_.Reset();
            Check(swapChain_->ResizeBuffers(0, static_cast<UINT>(width), static_cast<UINT>(height),
                                            DXGI_FORMAT_UNKNOWN, 0),
                  "Mirror resize");
            CreateMirrorTarget();
        }
        context_->OMSetRenderTargets(1, mirrorTarget_.GetAddressOf(), nullptr);
        context_->OMSetDepthStencilState(mirrorDepthState_.Get(), 0);
        const float clear[] = {.01f, .015f, .025f, 1};
        context_->ClearRenderTargetView(mirrorTarget_.Get(), clear);
        context_->IASetInputLayout(nullptr);
        context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context_->VSSetShader(mirrorVs_.Get(), nullptr, 0);
        context_->PSSetShader(mirrorPs_.Get(), nullptr, 0);
        context_->PSSetSamplers(0, 1, sampler_.GetAddressOf());
        for (int eye = 0; eye < 2; ++eye)
        {
            const auto halfWidth = static_cast<float>(width) * .5f;
            const auto scale =
                std::min(halfWidth / static_cast<float>(eyeWidth_),
                         static_cast<float>(height) / static_cast<float>(eyeHeight_));
            const auto viewWidth = static_cast<float>(eyeWidth_) * scale,
                       viewHeight = static_cast<float>(eyeHeight_) * scale;
            const D3D11_VIEWPORT viewport{eye * halfWidth + (halfWidth - viewWidth) * .5f,
                                          (static_cast<float>(height) - viewHeight) * .5f,
                                          viewWidth,
                                          viewHeight,
                                          0,
                                          1};
            context_->RSSetViewports(1, &viewport);
            context_->PSSetShaderResources(0, 1, eyes_[eye].view.GetAddressOf());
            context_->Draw(3, 0);
        }
        ID3D11ShaderResourceView *empty = nullptr;
        context_->PSSetShaderResources(0, 1, &empty);
        const auto result = swapChain_->Present(0, 0);
        if (result != DXGI_STATUS_OCCLUDED)
            Check(result, "Mirror present");
    }

    void LogFrame(vr::EVRCompositorError leftError, vr::EVRCompositorError rightError)
    {
        uint32_t prediction{}, gamePrediction{};
        compositor_->GetLastPosePredictionIDs(&prediction, &gamePrediction);
        log_ << "{\"event\":\"frame\",\"processId\":" << GetCurrentProcessId()
             << ",\"frame\":" << frame_ << ",\"elapsedSeconds\":" << Elapsed()
             << ",\"predictionId\":" << prediction
             << ",\"headValid\":" << (poses_[0].bPoseIsValid ? "true" : "false")
             << ",\"headConnected\":" << (poses_[0].bDeviceIsConnected ? "true" : "false")
             << ",\"leftSubmitError\":" << static_cast<int>(leftError)
             << ",\"rightSubmitError\":" << static_cast<int>(rightError)
             << ",\"routedHeadStandingMatrix\":[";
        for (int row = 0; row < 3; ++row)
            for (int column = 0; column < 4; ++column)
            {
                if (row || column)
                    log_ << ',';
                log_ << JsonNumber{poses_[0].mDeviceToAbsoluteTracking.m[row][column]};
            }
        log_ << "],\"controllers\":[";
        bool first = true;
        uint64_t buttons = 0;
        for (uint32_t index = 1; index < vr::k_unMaxTrackedDeviceCount; ++index)
        {
            if (system_->GetTrackedDeviceClass(index) != vr::TrackedDeviceClass_Controller)
                continue;
            if (!first)
                log_ << ',';
            first = false;
            vr::VRControllerState_t state{};
            const bool available = system_->GetControllerState(index, &state, sizeof(state));
            buttons |= state.ulButtonPressed;
            log_ << "{\"index\":" << index << ",\"role\":"
                 << static_cast<int>(system_->GetControllerRoleForTrackedDeviceIndex(index))
                 << ",\"poseValid\":" << (poses_[index].bPoseIsValid ? "true" : "false")
                 << ",\"legacyInputAvailable\":" << (available ? "true" : "false")
                 << ",\"buttons\":" << state.ulButtonPressed
                 << ",\"touched\":" << state.ulButtonTouched << ",\"standingPosition\":["
                 << JsonNumber{poses_[index].mDeviceToAbsoluteTracking.m[0][3]} << ','
                 << JsonNumber{poses_[index].mDeviceToAbsoluteTracking.m[1][3]} << ','
                 << JsonNumber{poses_[index].mDeviceToAbsoluteTracking.m[2][3]} << "],\"axes\":[";
            for (int axis = 0; axis < vr::k_unControllerStateAxisCount; ++axis)
            {
                if (axis)
                    log_ << ',';
                log_ << '[' << JsonNumber{state.rAxis[axis].x} << ','
                     << JsonNumber{state.rAxis[axis].y} << ']';
            }
            log_ << "]}";
        }
        log_ << ']';
        if (!mappingOpen_)
            mappingOpen_ = mapping_.Open(false);
        sw::Status driver{};
        if (mappingOpen_ && sw::ReadBlock(mapping_.status(), driver) && driver.magic == sw::Magic &&
            driver.version == sw::Version)
        {
            const auto age =
                static_cast<double>(sw::QpcNow() - driver.timestamp) * 1000 / sw::QpcFrequency();
            const bool alive = driver.timestamp > 0 && age >= 0 && age < 500;
            const bool capturedHeadAvailable =
                alive && (driver.reserved0 & 1) != 0 && driver.hasHead != 0 &&
                driver.headAgeMilliseconds >= 0 &&
                driver.headAgeMilliseconds <= sw::WatchdogMilliseconds;
            double syntheticHeadAge{};
            const bool syntheticHeadAvailable = SyntheticHeadAvailable(
                driver, alive, sw::QpcNow(), sw::QpcFrequency(), syntheticHeadAge);
            log_ << ",\"driver\":{\"epoch\":" << driver.ackEpoch
                 << ",\"mode\":" << driver.actualMode << ",\"error\":" << driver.error
                 << ",\"statusAgeMilliseconds\":" << age
                 << ",\"statusAlive\":" << (alive ? "true" : "false")
                 << ",\"capabilityFlags\":" << driver.reserved0
                 << ",\"physicalSamples\":" << driver.physicalSamples
                 << ",\"routedSamples\":" << driver.routedSamples
                 << ",\"headAgeMilliseconds\":" << JsonNumber{driver.headAgeMilliseconds}
                 << ",\"capturedPhysicalHeadAvailable\":"
                 << (capturedHeadAvailable ? "true" : "false")
                 << ",\"capturedPhysicalHeadWorldPosition\":";
            if (capturedHeadAvailable)
                log_ << '[' << JsonNumber{driver.position[0]} << ','
                     << JsonNumber{driver.position[1]} << ',' << JsonNumber{driver.position[2]}
                     << ']';
            else
                log_ << "null";
            log_ << ",\"capturedPhysicalHeadWorldQuaternionWxyz\":";
            if (capturedHeadAvailable)
            {
                log_ << '[';
                for (int i = 0; i < 4; ++i)
                {
                    if (i)
                        log_ << ',';
                    log_ << JsonNumber{driver.quaternion[i]};
                }
                log_ << ']';
            }
            else
                log_ << "null";
            log_ << ",\"anchorEpoch\":" << driver.anchorEpoch
                 << ",\"syntheticHeadEpoch\":" << driver.syntheticHeadEpoch
                 << ",\"syntheticHeadQpc\":" << driver.syntheticHeadQpc
                 << ",\"syntheticHeadAgeMilliseconds\":" << JsonNumber{syntheticHeadAge}
                 << ",\"routedSyntheticHeadAvailable\":" << (syntheticHeadAvailable ? "true" : "false")
                 << ",\"routedSyntheticWorldPosition\":";
            if (syntheticHeadAvailable)
                log_ << '[' << JsonNumber{driver.syntheticPosition[0]} << ','
                     << JsonNumber{driver.syntheticPosition[1]} << ','
                     << JsonNumber{driver.syntheticPosition[2]} << ']';
            else
                log_ << "null";
            log_ << ",\"routedSyntheticWorldQuaternionWxyz\":";
            if (syntheticHeadAvailable)
            {
                log_ << '[';
                for (int i = 0; i < 4; ++i)
                {
                    if (i)
                        log_ << ',';
                    log_ << JsonNumber{driver.syntheticQuaternion[i]};
                }
                log_ << ']';
            }
            else
                log_ << "null";
            log_ << '}';
        }
        else
            log_ << ",\"driver\":{\"statusAlive\":false,\"capturedPhysicalHeadAvailable\":false,"
                    "\"routedSyntheticHeadAvailable\":false,\"routedSyntheticWorldPosition\":null,"
                    "\"routedSyntheticWorldQuaternionWxyz\":null}";
        log_ << "}\n";
        if (frame_ % 100 < 10)
            log_.flush();
        std::wostringstream title;
        title << L"SWITCHEROONIE CONTROLLED TEST | frame " << frame_ << L" | head "
              << (poses_[0].bPoseIsValid ? L"valid" : L"INVALID") << L" | buttons 0x" << std::hex
              << buttons << std::dec << L" | Submit " << static_cast<int>(leftError) << L'/'
              << static_cast<int>(rightError) << L" | Esc closes test";
        SetWindowTextW(window_, title.str().c_str());
    }

    struct Eye
    {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11RenderTargetView> target;
        ComPtr<ID3D11ShaderResourceView> view;
        ComPtr<ID3D11DepthStencilView> depth;
    };
    vr::IVRSystem *system_{};
    vr::IVRCompositor *compositor_{};
    HWND window_{};
    bool quit_{}, resize_{}, mappingOpen_{};
    uint32_t eyeWidth_{}, eyeHeight_{};
    uint32_t featureLevel_{};
    uint64_t frame_{};
    int64_t start_{}, lastLog_{};
    sw::Mapping mapping_;
    std::ofstream log_;
    std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount> poses_{};
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<IDXGISwapChain> swapChain_;
    ComPtr<ID3D11RenderTargetView> mirrorTarget_;
    std::array<Eye, 2> eyes_;
    ComPtr<ID3D11VertexShader> geometryVs_, mirrorVs_;
    ComPtr<ID3D11PixelShader> geometryPs_, mirrorPs_;
    ComPtr<ID3D11InputLayout> layout_;
    ComPtr<ID3D11Buffer> transform_, grid_, cube_, controllerLines_;
    ComPtr<ID3D11RasterizerState> rasterizer_;
    ComPtr<ID3D11DepthStencilState> depthState_, mirrorDepthState_;
    ComPtr<ID3D11SamplerState> sampler_;
    UINT gridCount_{}, controllerLineCount_{};
};

int SelfTest()
{
    // These checks initialize neither OpenVR nor a D3D device or visible window.
    Compile(GeometryShader, "VS", "vs_5_0");
    Compile(GeometryShader, "PS", "ps_5_0");
    Compile(MirrorShader, "VS", "vs_5_0");
    Compile(MirrorShader, "PS", "ps_5_0");
    vr::HmdMatrix34_t pose{{{1, 0, 0, 1}, {0, 1, 0, 2}, {0, 0, 1, 3}}};
    XMFLOAT3 moved{};
    XMStoreFloat3(&moved, XMVector3TransformCoord(XMVectorSet(0, 0, -1, 1), PoseMatrix(pose)));
    if (std::abs(moved.x - 1) > 1e-6f || std::abs(moved.y - 2) > 1e-6f ||
        std::abs(moved.z - 2) > 1e-6f)
        throw std::runtime_error("Pose representation test failed");
    XMFLOAT3 back{};
    XMStoreFloat3(&back, XMVector3TransformCoord(XMLoadFloat3(&moved),
                                                 XMMatrixInverse(nullptr, PoseMatrix(pose))));
    if (std::abs(back.x) > 1e-6f || std::abs(back.y) > 1e-6f || std::abs(back.z + 1) > 1e-6f)
        throw std::runtime_error("Inverse view test failed");
    vr::HmdMatrix44_t matrix{{{2, 0, 0, 0}, {0, 3, 0, 0}, {0, 0, -1, -.1f}, {0, 0, -1, 0}}};
    XMFLOAT4 clip{};
    XMStoreFloat4(&clip, XMVector4Transform(XMVectorSet(1, 2, -4, 1), ProjectionMatrix(matrix)));
    if (std::abs(clip.x - 2) > 1e-6f || std::abs(clip.y - 6) > 1e-6f ||
        std::abs(clip.z - 3.9f) > 1e-6f || std::abs(clip.w - 4) > 1e-6f)
        throw std::runtime_error("Projection representation test failed");
    vr::HmdMatrix34_t head{{{1, 0, 0, 0}, {0, 1, 0, 1.6f}, {0, 0, 1, 0}}},
        eyeLeft{{{1, 0, 0, -.032f}, {0, 1, 0, 0}, {0, 0, 1, 0}}},
        eyeRight{{{1, 0, 0, .032f}, {0, 1, 0, 0}, {0, 0, 1, 0}}};
    const auto headView = XMMatrixInverse(nullptr, PoseMatrix(head));
    XMFLOAT3 left{}, right{};
    XMStoreFloat3(
        &left, XMVector3TransformCoord(XMVectorSet(0, 1.6f, -2, 1),
                                       headView * XMMatrixInverse(nullptr, PoseMatrix(eyeLeft))));
    XMStoreFloat3(
        &right, XMVector3TransformCoord(XMVectorSet(0, 1.6f, -2, 1),
                                        headView * XMMatrixInverse(nullptr, PoseMatrix(eyeRight))));
    if (std::abs(left.x - .032f) > 1e-6f || std::abs(right.x + .032f) > 1e-6f ||
        std::abs(left.y) > 1e-6f || std::abs(right.y) > 1e-6f)
        throw std::runtime_error("Stereo eye transform composition failed");
    sw::Status diagnostic{};
    diagnostic.actualMode = diagnostic.syntheticHeadValid = diagnostic.reserved0 = 1;
    diagnostic.ackEpoch = diagnostic.anchorEpoch = diagnostic.syntheticHeadEpoch = 42;
    diagnostic.syntheticHeadQpc = 1000000;
    double age{};
    if (!SyntheticHeadAvailable(diagnostic, true, 1000001, 1000000, age) ||
        SyntheticHeadAvailable(diagnostic, false, 1000001, 1000000, age) ||
        SyntheticHeadAvailable(diagnostic, true, 1200001, 1000000, age))
        throw std::runtime_error("Synthetic diagnostic freshness gate failed");
    diagnostic.syntheticHeadEpoch = 41;
    if (SyntheticHeadAvailable(diagnostic, true, 1000001, 1000000, age))
        throw std::runtime_error("Synthetic diagnostic epoch gate failed");
    diagnostic.syntheticHeadEpoch = 42;
    diagnostic.anchorEpoch = 41;
    if (SyntheticHeadAvailable(diagnostic, true, 1000001, 1000000, age))
        throw std::runtime_error("Synthetic diagnostic committed-anchor epoch gate failed");
    diagnostic.anchorEpoch = 42;
    diagnostic.actualMode = 0;
    if (SyntheticHeadAvailable(diagnostic, true, 1000001, 1000000, age))
        throw std::runtime_error("Synthetic diagnostic physical-mode gate failed");
    diagnostic.actualMode = 1;
    diagnostic.reserved0 = 0;
    if (SyntheticHeadAvailable(diagnostic, true, 1000001, 1000000, age))
        throw std::runtime_error("Synthetic diagnostic pose-hook capability gate failed");
    std::cout << "PASS: four real HLSL shader compilations; pose translation/inverse; projection "
                 "row-vector representation; stereo eye transform signs/composition; synthetic "
                 "diagnostic freshness/epoch/mode/capability gates. No "
                 "runtime/window/device initialized. Hardware stereo rendering NOT tested.\n";
    return 0;
}

} // namespace

int wmain(int argc, wchar_t **argv)
{
    try
    {
        bool run = false, selfTest = false;
        std::filesystem::path log;
        for (int i = 1; i < argc; ++i)
        {
            const std::wstring argument = argv[i];
            if (argument == L"--run")
                run = true;
            else if (argument == L"--self-test")
                selfTest = true;
            else if (argument == L"--log" && i + 1 < argc)
                log = argv[++i];
            else if (argument != L"--help")
                throw std::runtime_error("Unknown argument; use --help");
        }
        if (selfTest)
        {
            if (run)
                throw std::runtime_error("--self-test cannot be combined with --run");
            return SelfTest();
        }
        if (!run)
        {
            std::cout
                << "switcheroonie-test-scene --run [--log file.jsonl]\nExplicit controlled-scene "
                   "launch; SteamVR must already be running. Close other scene apps during a safe "
                   "test window first.\nSide-by-side stereo mirror, colored cubes/grid, controller "
                   "axes/rays, redacted frame/input logs. Escape closes only this "
                   "test.\n--self-test compiles shaders and checks matrix representations without "
                   "VR, GPU device, or visible window.\nNo automatic runtime launch. No VRChat "
                   "menu or headset-free/display-attachment certification.\n";
            return 0;
        }
        if (log.empty())
            log = DefaultLogPath();
        Scene scene;
        scene.Run(log);
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << "Controlled scene: " << error.what() << '\n';
        return 2;
    }
}

