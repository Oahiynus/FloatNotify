#include <windows.h>
#include <unknwn.h>
#include <dispatcherqueue.h>
#include <d2d1effects.h>
#include <dwmapi.h>
#include <magnification.h>
#include "GlassCapture.h"
#include <cmath>
#pragma comment(lib,"Magnification.lib")
#pragma comment(lib,"gdi32.lib")
#pragma comment(lib,"user32.lib")
#include <windows.graphics.effects.interop.h>
#include <windows.ui.composition.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <winrt/Windows.System.h>
#include <algorithm>
#include <array>
#include <memory>
#include <vector>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Graphics::Effects;
using namespace Windows::UI::Composition;
using namespace Windows::UI::Composition::Desktop;
namespace abiEffects = ABI::Windows::Graphics::Effects;

// Windows Composition reads this small Direct2D graph directly. The source is
// the compositor's live backdrop, so the process never captures desktop pixels.
struct Effect : implements<Effect, IGraphicsEffect, IGraphicsEffectSource, abiEffects::IGraphicsEffectD2D1Interop>
{
    GUID id;
    std::vector<IPropertyValue> properties;
    IGraphicsEffectSource input;
    hstring name;
    Effect(GUID cls, std::vector<IPropertyValue> values, IGraphicsEffectSource const& source)
        : id(cls), properties(std::move(values)), input(source) {}
    hstring Name() const { return name; }
    void Name(hstring const& value) { name=value; }
    HRESULT __stdcall GetEffectId(GUID* out) noexcept final { *out=id; return S_OK; }
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR, UINT*, abiEffects::GRAPHICS_EFFECT_PROPERTY_MAPPING*) noexcept final { return E_INVALIDARG; }
    HRESULT __stdcall GetPropertyCount(UINT* out) noexcept final { *out=(UINT)properties.size();return S_OK; }
    HRESULT __stdcall GetProperty(UINT index,ABI::Windows::Foundation::IPropertyValue** out) noexcept final {
        *out=nullptr; if(index>=properties.size())return E_BOUNDS;
        return winrt::get_unknown(properties[index])->QueryInterface(IID_PPV_ARGS(out));
    }
    HRESULT __stdcall GetSourceCount(UINT* out) noexcept final { *out=1;return S_OK; }
    HRESULT __stdcall GetSource(UINT index,abiEffects::IGraphicsEffectSource** out) noexcept final {
        *out=nullptr; if(index!=0)return E_BOUNDS;
        return winrt::get_unknown(input)->QueryInterface(IID_PPV_ARGS(out));
    }
};
static IPropertyValue integer(unsigned v){return PropertyValue::CreateUInt32(v).as<IPropertyValue>();}
static auto& compositor(){
    // WPF/WinForms already pumps this STA. Keep its dispatcher for the resident's lifetime.
    static const auto queue=[] {
        auto q=Windows::System::DispatcherQueue::GetForCurrentThread();
        Windows::System::DispatcherQueueController controller{nullptr};
        if(!q)check_hresult(CreateDispatcherQueueController({sizeof(DispatcherQueueOptions),DQTYPE_THREAD_CURRENT,DQTAT_COM_STA},
            reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(put_abi(controller))));
        return controller;
    }();
    static Compositor instance;return instance;
}
// The compositor allows blur but disallows transforming a host backdrop. A windowed
// magnifier supplies only the narrow optical rim, with our own surfaces excluded.
struct RimLens {
    HWND frame{},lens{};
    int lastW{},lastH{},lastRadius{};
    GlassCapture capture;
    ~RimLens(){capture.detach();if(frame)DestroyWindow(frame);}
    bool place(HWND card,HWND plate,int x,int y,int w,int h,float dpi,float radius){
        if(!capture.ready())return false;
        constexpr float zoom=1.085f;
        if(!frame){
            frame=CreateWindowExW(WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST,
                L"STATIC",L"",WS_POPUP|WS_CLIPCHILDREN,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
            if(!frame || !SetLayeredWindowAttributes(frame,0,204,LWA_ALPHA))return false;
            lens=CreateWindowW(L"Magnifier",L"",WS_VISIBLE|WS_CHILD,0,0,1,1,frame,nullptr,GetModuleHandleW(nullptr),nullptr);
            MAGTRANSFORM transform{};transform.v[0][0]=transform.v[1][1]=zoom;transform.v[2][2]=1;
            if(!lens || !MagSetWindowTransform(lens,&transform) || !capture.attach(lens,card,frame,plate))return false;
        }
        if(!capture.usable())return false;
        int r=(int)std::round(radius*dpi),band=(int)std::round(6*dpi);
        if(w!=lastW||h!=lastH||r!=lastRadius){
            HRGN ring=CreateRoundRectRgn(0,0,w+1,h+1,2*r,2*r);
            HRGN center=CreateRoundRectRgn(band,band,w-band+1,h-band+1,2*(r-band),2*(r-band));
            if(!ring||!center){if(ring)DeleteObject(ring);if(center)DeleteObject(center);return false;}
            CombineRgn(ring,ring,center,RGN_DIFF);DeleteObject(center);
            if(!SetWindowRgn(frame,ring,FALSE)){DeleteObject(ring);return false;}
            SetWindowPos(lens,nullptr,0,0,w,h,SWP_NOACTIVATE|SWP_NOZORDER);
            lastW=w;lastH=h;lastRadius=r;
        }
        int sourceW=(int)std::ceil(w/zoom),sourceH=(int)std::ceil(h/zoom);
        int left=x+(w-sourceW)/2,top=y+(h-sourceH)/2;
        if(!MagSetWindowSource(lens,{left,top,left+sourceW,top+sourceH}))return false;
        return SetWindowPos(frame,card,x,y,w,h,SWP_NOACTIVATE|SWP_SHOWWINDOW)!=FALSE;
    }
};
struct Surface {
    HWND plate;
    std::unique_ptr<RimLens> rim;
    Compositor c{nullptr};
    DesktopWindowTarget target{nullptr};
    CompositionBackdropBrush backdrop{nullptr};
    explicit Surface(HWND hwnd): plate(hwnd),c(compositor()) {
        BOOL enable=TRUE;
        check_hresult(DwmSetWindowAttribute(hwnd,DWMWA_USE_HOSTBACKDROPBRUSH,&enable,sizeof enable));
        check_hresult(c.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>()->CreateDesktopWindowTarget(hwnd,TRUE,
            reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(put_abi(target))));
        backdrop=c.CreateHostBackdropBrush();
    }
    CompositionBrush brush(IGraphicsEffect const& effect) {
        auto b=c.CreateEffectFactory(effect).CreateBrush();
        b.SetSourceParameter(L"desktop",backdrop);return b;
    }
    void paint(int width,int height,float dpi,float radius,bool frosted){
        auto root=c.CreateContainerVisual();
        CompositionEffectSourceParameter source(L"desktop");
        auto layer=[&](float inset,CompositionBrush const& brush){
            auto geometry=c.CreateRoundedRectangleGeometry();
            float r=(std::max)(1.f,radius*dpi-inset);
            geometry.Offset({inset,inset});geometry.Size({width-2*inset,height-2*inset});geometry.CornerRadius({r,r});
            auto sprite=c.CreateSpriteVisual();sprite.Size({(float)width,(float)height});
            sprite.Clip(c.CreateGeometricClip(geometry));sprite.Brush(brush);
            root.Children().InsertAtTop(sprite);
        };
        auto blur=make<Effect>(CLSID_D2D1GaussianBlur,std::vector<IPropertyValue>{
            PropertyValue::CreateSingle((frosted?12.f:2.5f)*dpi).as<IPropertyValue>(),integer(1),integer(1)},source);
        layer(0.f,brush(blur));
        if(frosted)rim.reset();
        target.Root(root);
    }
};
extern "C" __declspec(dllexport) HRESULT GlassCreate(HWND hwnd,Surface** out) noexcept {
    *out=nullptr;try{*out=new Surface(hwnd);return S_OK;}catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT GlassDraw(Surface* s,int w,int h,float dpi,float radius,BOOL soft) noexcept {
    try{s->paint(w,h,dpi,radius,soft!=FALSE);return S_OK;}catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) BOOL GlassPlaceRim(Surface* s,HWND card,int x,int y,int w,int h,float scale,float radius) noexcept {
    try{
        if(!s->rim)s->rim=std::make_unique<RimLens>();
        if(s->rim->place(card,s->plate,x,y,w,h,scale,radius))return TRUE;
        s->rim.reset();
    }catch(...){s->rim.reset();}
    return FALSE;
}
extern "C" __declspec(dllexport) void GlassDestroy(Surface* s) noexcept {delete s;}
