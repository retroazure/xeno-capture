// Xeno Capture native GPU bridge. Uses NVIDIA RTX Video SDK headers/libraries.
// NVIDIA SDK components are governed by NVIDIA_RTX_Video_SDK_License.pdf.
#include <windows.h>
#include <d3d11_1.h>
#include <d3d10.h>
#include <wrl/client.h>
#include <nvsdk_ngx_helpers_vsr.h>
#include <nvOpticalFlowD3D11.h>
#include <mutex>
#include <atomic>
#include <string>
#include <memory>
#include <vector>
#include <cstdio>
#include <cmath>
#include <algorithm>
using Microsoft::WRL::ComPtr;
static std::mutex ngxLifecycle;
static std::atomic<bool> ngxQuarantined{false};
static void NVSDK_CONV QuietLog(const char*, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature) {}
struct ContextScope {
    ID3D11DeviceContext1* context;ComPtr<ID3DDeviceContextState> previous;
    ContextScope(ID3D11DeviceContext1* c,ID3DDeviceContextState* state):context(c){if(context&&state)context->SwapDeviceContextState(state,&previous);else context=nullptr;}
    ~ContextScope(){if(context)context->SwapDeviceContextState(previous.Get(),nullptr);}
};
static HRESULT CreateIsolatedState(ID3D11Device* device,ID3D11DeviceContext* context,ComPtr<ID3D11DeviceContext1>& context1,ComPtr<ID3DDeviceContextState>& isolated) {
    HRESULT hr=context->QueryInterface(IID_PPV_ARGS(&context1));if(FAILED(hr))return hr;
    ComPtr<ID3D11Device1> device1;hr=device->QueryInterface(IID_PPV_ARGS(&device1));if(FAILED(hr))return hr;
    D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0},selected;
    return device1->CreateDeviceContextState(0,levels,2,D3D11_SDK_VERSION,__uuidof(ID3D11Device),&selected,&isolated);
}
// SDK feature memory must outlive queued inference and the draw reading its output.
// This fence is used at teardown only; live evaluation remains asynchronous.
static bool WaitForGpuIdle(ID3D11Device* device,ID3D11DeviceContext* context) noexcept {
    if(!device||!context||FAILED(device->GetDeviceRemovedReason()))return true;
    D3D11_QUERY_DESC desc={D3D11_QUERY_EVENT,0};ComPtr<ID3D11Query> query;
    if(FAILED(device->CreateQuery(&desc,&query)))return FAILED(device->GetDeviceRemovedReason());
    context->End(query.Get());context->Flush();ULONGLONG start=GetTickCount64();
    for(;;){BOOL ready=FALSE;HRESULT hr=context->GetData(query.Get(),&ready,sizeof(ready),D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if(hr==S_OK&&ready)return true;
        if(FAILED(hr))return FAILED(device->GetDeviceRemovedReason());
        if(FAILED(device->GetDeviceRemovedReason()))return true;
        if(GetTickCount64()-start>5000)return false;
        Sleep(1);
    }
}
struct VsrSession {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11DeviceContext1> context1;
    ComPtr<ID3D10Multithread> multithread;
    ComPtr<ID3DDeviceContextState> isolated;
    // Retain every object and path the asynchronous SDK can still reference.
    ComPtr<ID3D11Texture2D> lastInput,lastOutput;
    std::wstring modelDirectory,cacheDirectory;const wchar_t* modelPaths[1]={};NVSDK_NGX_FeatureCommonInfo commonInfo={};
    NVSDK_NGX_Parameter* parameters=nullptr;
    NVSDK_NGX_Handle* feature=nullptr;
    bool initialized=false, previousProtection=false;
    ~VsrSession() {
        std::lock_guard<std::mutex> lock(ngxLifecycle);
        ContextScope state(context1.Get(),isolated.Get());
        if(feature) NVSDK_NGX_D3D11_ReleaseFeature(feature);
        if(parameters && initialized) NVSDK_NGX_D3D11_DestroyParameters(parameters);
        if(initialized) NVSDK_NGX_D3D11_Shutdown1(device.Get());
        if(multithread) multithread->SetMultithreadProtected(previousProtection);
    }
};
struct IsolatedState {
    VsrSession* session; ComPtr<ID3DDeviceContextState> previous;
    explicit IsolatedState(VsrSession* s):session(s) { s->context1->SwapDeviceContextState(s->isolated.Get(),&previous); }
    ~IsolatedState() { session->context1->SwapDeviceContextState(previous.Get(),nullptr); }
};
static void SafeVsrDelete(VsrSession* session) noexcept {
    if(!session)return;
    if(ngxQuarantined.load()&&session->initialized)return;
    if(session->feature&&!WaitForGpuIdle(session->device.Get(),session->context.Get())) {
        // Keep the outstanding session alive rather than free memory still used by
        // a nonresponsive GPU. Block new SDK sessions for this process. The OS
        // reclaims this exceptional retained session when the app exits.
        ngxQuarantined.store(true);return;
    }
    delete session;
}
extern "C" __declspec(dllexport) unsigned XenoVsrCreate(ID3D11Device* device,const wchar_t* modelPath,const wchar_t* cachePath,void** output) noexcept {
    if(!device || !modelPath || !cachePath || !output) return 0xBAD00005;
    *output=nullptr;
    try {
        std::unique_ptr<VsrSession,decltype(&SafeVsrDelete)> s(new VsrSession,&SafeVsrDelete);
        std::lock_guard<std::mutex> sdkLock(ngxLifecycle);
        if(ngxQuarantined.load())return 0xBAD0A001;
        s->device=device; device->GetImmediateContext(&s->context);
        if(FAILED(s->context.As(&s->context1))) return 0xBAD00002;
        if(SUCCEEDED(s->context.As(&s->multithread))) s->previousProtection=s->multithread->SetMultithreadProtected(TRUE)!=FALSE;
        ComPtr<ID3D11Device1> device1; if(FAILED(device->QueryInterface(IID_PPV_ARGS(&device1)))) return 0xBAD00002;
        D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0}; D3D_FEATURE_LEVEL selected;
        if(FAILED(device1->CreateDeviceContextState(0,levels,2,D3D11_SDK_VERSION,__uuidof(ID3D11Device),&selected,&s->isolated))) return 0xBAD00002;
        IsolatedState state(s.get());
        s->modelDirectory=modelPath;s->cacheDirectory=cachePath;s->modelPaths[0]=s->modelDirectory.c_str();
        auto& info=s->commonInfo;info.PathListInfo.Path=s->modelPaths;info.PathListInfo.Length=1;
        info.LoggingInfo.LoggingCallback=QuietLog;info.LoggingInfo.MinimumLoggingLevel=NVSDK_NGX_LOGGING_LEVEL_OFF;info.LoggingInfo.DisableOtherLoggingSinks=true;
        NVSDK_NGX_Result result;
        {
            result=NVSDK_NGX_D3D11_Init(0,s->cacheDirectory.c_str(),device,&info);
            if(NVSDK_NGX_FAILED(result)) return result;
            s->initialized=true;
            result=NVSDK_NGX_D3D11_GetCapabilityParameters(&s->parameters);
            if(NVSDK_NGX_FAILED(result)) return result;
        }
        int available=0; s->parameters->Get(NVSDK_NGX_Parameter_VSR_Available,&available);
        if(!available) return 0xBAD00001;
        NVSDK_NGX_Feature_Create_Params create={};
        result=NGX_D3D11_CREATE_VSR_EXT(s->context.Get(),&s->feature,s->parameters,&create);
        if(NVSDK_NGX_FAILED(result)) return result;
        *output=s.release(); return NVSDK_NGX_Result_Success;
    } catch(...) { return 0xBAD00000; }
}
extern "C" __declspec(dllexport) unsigned XenoVsrEvaluate(void* handle,ID3D11Texture2D* input,ID3D11Texture2D* output,unsigned iw,unsigned ih,unsigned ow,unsigned oh,unsigned quality) noexcept {
    if(!handle || !input || !output || !iw || !ih || !ow || !oh || quality>4) return 0xBAD00005;
    try {
        std::lock_guard<std::mutex> sdkLock(ngxLifecycle);if(ngxQuarantined.load())return 0xBAD0A001;
        auto s=static_cast<VsrSession*>(handle); IsolatedState state(s);s->lastInput=input;s->lastOutput=output;
        NVSDK_NGX_D3D11_VSR_Eval_Params parameters={}; parameters.pInput=input; parameters.pOutput=output;
        parameters.InputSubrectSize={iw,ih}; parameters.OutputSubrectSize={ow,oh}; parameters.QualityLevel=static_cast<NVSDK_NGX_VSR_QualityLevel>(quality);
        return NGX_D3D11_EVALUATE_VSR_EXT(s->context.Get(),s->feature,s->parameters,&parameters);
    } catch(...) { return 0xBAD00000; }
}
extern "C" __declspec(dllexport) unsigned XenoVsrSynchronize(void* handle) noexcept {
    if(!handle)return 0xBAD00005;auto session=static_cast<VsrSession*>(handle);
    if(WaitForGpuIdle(session->device.Get(),session->context.Get()))return 1;
    ngxQuarantined.store(true);return 0xBAD0A001;
}
extern "C" __declspec(dllexport) void XenoVsrDestroy(void* handle) noexcept { try { SafeVsrDelete(static_cast<VsrSession*>(handle)); } catch(...) {} }
struct OpticalFlowSession {
    NV_OF_D3D11_API_FUNCTION_LIST api={}; NvOFHandle handle=nullptr;
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11DeviceContext1> context1;ComPtr<ID3DDeviceContextState> isolated;
    ComPtr<ID3D11Texture2D> vectors[2],costs[2];
    NvOFGPUBufferHandle inputs[2]={},output[2]={},cost[2]={};
    ~OpticalFlowSession() {
        ContextScope state(context1.Get(),isolated.Get());
        if(handle) { for(auto value:inputs) if(value) api.nvOFUnregisterResourceD3D11(value); for(auto value:output) if(value) api.nvOFUnregisterResourceD3D11(value); for(auto value:cost) if(value) api.nvOFUnregisterResourceD3D11(value); api.nvOFDestroy(handle); }
    }
};
static HMODULE OpticalFlowModule() {
    static HMODULE module = LoadLibraryExW(L"nvofapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    return module;
}
extern "C" __declspec(dllexport) int XenoOfCreate(ID3D11Device* device,ID3D11Texture2D* first,ID3D11Texture2D* second,unsigned width,unsigned height,void** outSession,ID3D11Texture2D** forward,ID3D11Texture2D** backward,ID3D11Texture2D** forwardCost,ID3D11Texture2D** backwardCost,unsigned* gridWidth,unsigned* gridHeight) noexcept {
    if(!device||!first||!second||!outSession||!forward||!backward||!forwardCost||!backwardCost||!gridWidth||!gridHeight) return -1;
    *outSession=nullptr;*forward=*backward=*forwardCost=*backwardCost=nullptr;
    D3D11_TEXTURE2D_DESC firstDesc={},secondDesc={};first->GetDesc(&firstDesc);second->GetDesc(&secondDesc);
    if(firstDesc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM||secondDesc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM||firstDesc.Width!=width||secondDesc.Width!=width||firstDesc.Height!=height||secondDesc.Height!=height)return -7;
    try {
        auto module=OpticalFlowModule(); if(!module) return -2;
        using Load=NV_OF_STATUS(NVOFAPI*)(uint32_t,NV_OF_D3D11_API_FUNCTION_LIST*);
        auto load=reinterpret_cast<Load>(GetProcAddress(module,"NvOFAPICreateInstanceD3D11")); if(!load) return -3;
        auto s=std::make_unique<OpticalFlowSession>(); auto result=load(NV_OF_API_VERSION,&s->api); if(result!=NV_OF_SUCCESS) return result;
        const auto& api = s->api;
        if (!api.nvCreateOpticalFlowD3D11 || !api.nvOFInit || !api.nvOFRegisterResourceD3D11
            || !api.nvOFUnregisterResourceD3D11 || !api.nvOFExecute || !api.nvOFDestroy)
            return NV_OF_ERR_UNSUPPORTED_FEATURE;
#ifdef XENO_GPU_PROBE
        if (!api.nvOFGetSurfaceFormatCountD3D11 || !api.nvOFGetSurfaceFormatD3D11)
            return NV_OF_ERR_UNSUPPORTED_FEATURE;
#endif
        s->device=device;device->GetImmediateContext(&s->context);
        if(FAILED(CreateIsolatedState(device,s->context.Get(),s->context1,s->isolated)))return -8;
        ContextScope state(s->context1.Get(),s->isolated.Get());
        result=s->api.nvCreateOpticalFlowD3D11(device,s->context.Get(),&s->handle); if(result!=NV_OF_SUCCESS) return result;
#ifdef XENO_GPU_PROBE
        for(int u=1;u<=4;u++){uint32_t count=0;s->api.nvOFGetSurfaceFormatCountD3D11(s->handle,static_cast<NV_OF_BUFFER_USAGE>(u),NV_OF_MODE_OPTICALFLOW,&count);std::vector<DXGI_FORMAT> formats(count);if(count)s->api.nvOFGetSurfaceFormatD3D11(s->handle,static_cast<NV_OF_BUFFER_USAGE>(u),NV_OF_MODE_OPTICALFLOW,formats.data());printf("NVOF usage %d formats:",u);for(auto fmt:formats)printf(" %d",int(fmt));printf("\n");}
#endif
        NV_OF_INIT_PARAMS init={};init.width=width;init.height=height;init.outGridSize=NV_OF_OUTPUT_VECTOR_GRID_SIZE_4;init.mode=NV_OF_MODE_OPTICALFLOW;init.perfLevel=NV_OF_PERF_LEVEL_MEDIUM;init.enableOutputCost=NV_OF_TRUE;init.predDirection=NV_OF_PRED_DIRECTION_BOTH;init.inputBufferFormat=NV_OF_BUFFER_FORMAT_ABGR8;
        result=s->api.nvOFInit(s->handle,&init);if(result!=NV_OF_SUCCESS) return result;
        result=s->api.nvOFRegisterResourceD3D11(s->handle,first,&s->inputs[0]);if(result!=NV_OF_SUCCESS)return result;
        result=s->api.nvOFRegisterResourceD3D11(s->handle,second,&s->inputs[1]);if(result!=NV_OF_SUCCESS)return result;
        unsigned gw=(width+3)/4,gh=(height+3)/4;
        for(int i=0;i<2;i++) {
            D3D11_TEXTURE2D_DESC desc={};desc.Width=gw;desc.Height=gh;desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;desc.Format=DXGI_FORMAT_R16G16_SINT;
            if(FAILED(device->CreateTexture2D(&desc,nullptr,&s->vectors[i])))return -4;
            desc.Format=DXGI_FORMAT_R8_UINT;if(FAILED(device->CreateTexture2D(&desc,nullptr,&s->costs[i])))return -5;
            result=s->api.nvOFRegisterResourceD3D11(s->handle,s->vectors[i].Get(),&s->output[i]);if(result!=NV_OF_SUCCESS)return result;
            result=s->api.nvOFRegisterResourceD3D11(s->handle,s->costs[i].Get(),&s->cost[i]);if(result!=NV_OF_SUCCESS)return result;
        }
        s->vectors[0].CopyTo(forward);s->vectors[1].CopyTo(backward);s->costs[0].CopyTo(forwardCost);s->costs[1].CopyTo(backwardCost);*gridWidth=gw;*gridHeight=gh;*outSession=s.release();return 0;
    } catch(...) { return -6; }
}
extern "C" __declspec(dllexport) int XenoOfEvaluate(void* handle,unsigned previous,unsigned current) noexcept {
    if(!handle||previous>1||current>1||previous==current)return -1;
    auto s=static_cast<OpticalFlowSession*>(handle);ContextScope state(s->context1.Get(),s->isolated.Get());NV_OF_EXECUTE_INPUT_PARAMS input={};input.inputFrame=s->inputs[previous];input.referenceFrame=s->inputs[current];input.disableTemporalHints=NV_OF_TRUE;
    NV_OF_EXECUTE_OUTPUT_PARAMS output={};output.outputBuffer=s->output[0];output.outputCostBuffer=s->cost[0];output.bwdOutputBuffer=s->output[1];output.bwdOutputCostBuffer=s->cost[1];
    s->context->Flush();return s->api.nvOFExecute(s->handle,&input,&output);
}
extern "C" __declspec(dllexport) void XenoOfDestroy(void* handle) noexcept {try{delete static_cast<OpticalFlowSession*>(handle);}catch(...) {}}
#ifdef XENO_GPU_PROBE
static std::vector<unsigned char> Readback(ID3D11Device* device,ID3D11DeviceContext* context,ID3D11Texture2D* texture,unsigned bytes=4) {
    D3D11_TEXTURE2D_DESC desc; texture->GetDesc(&desc); desc.Usage=D3D11_USAGE_STAGING; desc.BindFlags=0; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> read; if(FAILED(device->CreateTexture2D(&desc,nullptr,&read))) return {};
    context->CopyResource(read.Get(),texture); D3D11_MAPPED_SUBRESOURCE mapped={}; if(FAILED(context->Map(read.Get(),0,D3D11_MAP_READ,0,&mapped))) return {};
    std::vector<unsigned char> pixels(desc.Width*desc.Height*bytes);
    for(unsigned y=0;y<desc.Height;y++) memcpy(pixels.data()+y*desc.Width*bytes,static_cast<unsigned char*>(mapped.pData)+y*mapped.RowPitch,desc.Width*bytes);
    context->Unmap(read.Get(),0); return pixels;
}
int wmain(int count,wchar_t** args) {
    if(count!=3) return 2;
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; D3D_FEATURE_LEVEL level;
    HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context);
    if(FAILED(hr)) { printf("D3D11 create: %08X\n",unsigned(hr)); return 3; }
    constexpr unsigned iw=640,ih=360,ow=1280,oh=720;
    std::vector<unsigned char> image(iw*ih*4);
    for(unsigned y=0;y<ih;y++) for(unsigned x=0;x<iw;x++) {
        unsigned i=(y*iw+x)*4; unsigned h=(x/8)*73856093u^(y/8)*19349663u;h^=h>>13;h*=1274126177u;h^=h>>16;
        image[i]=32+(h&127);image[i+1]=32+((h>>8)&127);image[i+2]=32+((h>>16)&127);image[i+3]=255;
    }
    D3D11_TEXTURE2D_DESC desc={}; desc.Width=iw;desc.Height=ih;desc.MipLevels=1;desc.ArraySize=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data={image.data(),iw*4,0}; ComPtr<ID3D11Texture2D> input,output;
    hr=device->CreateTexture2D(&desc,&data,&input); if(FAILED(hr)) return 4;
    std::vector<unsigned char> shifted(image.size());
    for(unsigned y=0;y<ih;y++) for(unsigned x=0;x<iw;x++) memcpy(shifted.data()+(y*iw+x)*4,image.data()+(y*iw+(x>=8 ? x-8:0))*4,4);
    desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;data.pSysMem=image.data();ComPtr<ID3D11Texture2D> flowInput;device->CreateTexture2D(&desc,&data,&flowInput);
    data.pSysMem=shifted.data();ComPtr<ID3D11Texture2D> second;device->CreateTexture2D(&desc,&data,&second);
    void* flow=nullptr;ComPtr<ID3D11Texture2D> f,b,fc,bc;unsigned gw=0,gh=0;
    int created=XenoOfCreate(device.Get(),flowInput.Get(),second.Get(),iw,ih,&flow,&f,&b,&fc,&bc,&gw,&gh);
    printf("NVOF typed create: status=%d grid=%ux%u\n",created,gw,gh);fflush(stdout);
    int evaluated=flow ? XenoOfEvaluate(flow,0,1):-1;
    printf("NVOF typed evaluate: status=%d\n",evaluated);fflush(stdout);
    bool flowCorrect=false;
    if(evaluated==0) {
        auto vf=Readback(device.Get(),context.Get(),f.Get()),vb=Readback(device.Get(),context.Get(),b.Get()),cost=Readback(device.Get(),context.Get(),fc.Get(),1);
        std::vector<float> xs,bs,cs;
        for(unsigned y=8;y+8<gh;y++) for(unsigned x=8;x+8<gw;x++) {unsigned i=y*gw+x;xs.push_back(reinterpret_cast<short*>(vf.data())[i*2]/32.f);bs.push_back(reinterpret_cast<short*>(vb.data())[i*2]/32.f);cs.push_back(cost[i]);}
        std::sort(xs.begin(),xs.end());std::sort(bs.begin(),bs.end());std::sort(cs.begin(),cs.end());
        printf("NVOF median: forward=%.3f backward=%.3f cost=%.1f p90cost=%.1f\n",xs[xs.size()/2],bs[bs.size()/2],cs[cs.size()/2],cs[cs.size()*9/10]);
        flowCorrect=fabs(xs[xs.size()/2]-8)<1 && fabs(bs[bs.size()/2]+8)<1;
    }
    XenoOfDestroy(flow);
    desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.Width=ow;desc.Height=oh;desc.BindFlags=D3D11_BIND_UNORDERED_ACCESS|D3D11_BIND_SHADER_RESOURCE; hr=device->CreateTexture2D(&desc,nullptr,&output); if(FAILED(hr)) return 5;
    void* session=nullptr; unsigned result=XenoVsrCreate(device.Get(),args[1],args[2],&session); printf("VSR SDK create: %08X\n",result); fflush(stdout);
    if(!session) return 6;
    unsigned low=XenoVsrEvaluate(session,input.Get(),output.Get(),iw,ih,ow,oh,0); auto baseline=Readback(device.Get(),context.Get(),output.Get());
    unsigned high=XenoVsrEvaluate(session,input.Get(),output.Get(),iw,ih,ow,oh,2); auto enhanced=Readback(device.Get(),context.Get(),output.Get());
    unsigned long long changed=0,error=0;
    if(baseline.size()==enhanced.size()) for(size_t i=0;i<enhanced.size();i++) { int delta=abs(int(baseline[i])-int(enhanced[i])); error+=delta; if(delta) changed++; }
    printf("VSR evaluate: bicubic=%08X medium=%08X changedBytes=%llu absoluteDifference=%llu\n",low,high,changed,error); fflush(stdout);
    XenoVsrDestroy(session); return low==1 && high==1 && changed>100 && flowCorrect ? 0:7;
}
#endif