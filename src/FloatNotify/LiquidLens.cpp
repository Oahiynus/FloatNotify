#include <windows.h>
#include <magnification.h>
#include "GlassCapture.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>
#pragma comment(lib,"Magnification.lib")

namespace {
struct Zone { float left,top,right,bottom; int kind; }; // 0 text, 1 option/editor, 2 button
struct Pixel { unsigned char b,g,r,a; };
struct Optics { float x,y,tint,rim,ink,reflection,shade,control; };
using Image=std::vector<Pixel>;
float unit(float x){return std::clamp(x,0.f,1.f);}
float square(float x){return x*x;}
float roundedDepth(float qx,float qy,float r){
    return r-std::hypot(std::max(0.f,qx),std::max(0.f,qy))-std::min(std::max(qx,qy),0.f);
}
float softAxis(float x){return 4.f*std::log1p(std::exp(std::clamp(x*.25f,-60.f,60.f)));}
enum class Surface { Liquid, Adaptive, TonalLiquid, Lustre };
float luma(Pixel p){return .2126f*p.r+.7152f*p.g+.0722f*p.b;}
float nightWeight(float level){float t=unit((178.f-level)/62.f);return t*t*(3-2*t);}
struct SamplingWindow;
thread_local SamplingWindow* activeSample{};
struct SamplingWindow {
    HWND host{},child{};
    GlassCapture capture;
    bool received{},changed{};
    int reason{-12};
    int sourceW{},sourceH{},w{},h{};
    float density{};
    bool hud{},needsLightInk{};
    Surface surface{};
    std::vector<unsigned char> inkMask;
    int maskW{},maskH{};
    std::vector<Zone> zones;
    std::vector<Optics> map;
    Image raw,nearBlur,farBlur,scratch,out,ambient,tiles,tileSamples;
    ~SamplingWindow(){capture.detach();if(host)DestroyWindow(host);}
    bool open(HWND card,int sw,int sh){
        if(!capture.ready())return false;
        host=CreateWindowExW(WS_EX_LAYERED|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TRANSPARENT,L"STATIC",L"",WS_POPUP,0,0,sw,sh,nullptr,nullptr,nullptr,nullptr);
        if(!host)return false;
        child=CreateWindowW(L"Magnifier",L"",WS_CHILD|WS_VISIBLE,0,0,sw,sh,host,nullptr,nullptr,nullptr);
        if(!child)return false;
        return MagSetImageScalingCallback(child,read) && capture.attach(child,card,nullptr);
    }
    static BOOL CALLBACK read(HWND,void* src,MAGIMAGEHEADER hdr,void*,MAGIMAGEHEADER,RECT,RECT,HRGN){
        // Windows can pass an internal HWND here. The active synchronous request owns the frame.
        auto s=activeSample;
        if(!s)return TRUE;
        if(!src){s->reason=-13;return FALSE;}
        if(hdr.width!=s->sourceW || hdr.height!=s->sourceH){s->reason=-14;return FALSE;}
        if(hdr.stride<hdr.width*4 || hdr.offset+size_t(hdr.height-1)*hdr.stride+hdr.width*4>hdr.cbSize){s->reason=-15;return FALSE;}
        const auto data=static_cast<const unsigned char*>(src)+hdr.offset;
        // The windowed magnifier delivers desktop DIB pixels in B,G,R byte order.
        for(int y=0;y<s->h;++y){
            int sy=(int)((y+.5)*s->sourceH/s->h);
            const auto row=reinterpret_cast<const Pixel*>(data+size_t(sy)*hdr.stride);
            auto dest=s->raw.data()+y*s->w;
            if(s->sourceW==s->w){
                if(std::memcmp(dest,row,s->w*sizeof(Pixel))){s->changed=true;std::memcpy(dest,row,s->w*sizeof(Pixel));}
            }else for(int x=0;x<s->w;++x){
                auto p=row[(int)((x+.5)*s->sourceW/s->w)];
                if(std::memcmp(&dest[x],&p,sizeof p)){s->changed=true;dest[x]=p;}
            }
        }
        s->received=true;return TRUE;
    }
    void configureMap(){
        float width=w/density,height=h/density,r=std::min({hud?29.f:30.f,width/2,height/2});
        float roll=std::min(26.f,height*.27f),strength=hud?16.f:27.f;
        for(int y=0;y<h;++y)for(int x=0;x<w;++x){
            float px=(x+.5f)/density,py=(y+.5f)/density;
            float xx=px-width/2,yy=py-height/2;
            float qx=std::abs(xx)-width/2+r,qy=std::abs(yy)-height/2+r;
            float d=std::max(0.f,roundedDepth(qx,qy,r));
            float vx=softAxis(qx),vy=softAxis(qy),norm=std::max(1.e-25f,std::hypot(vx,vy));
            float nx=std::copysign(vx/norm,xx),ny=std::copysign(vy/norm,yy);
            float slope=std::exp(-d/roll),u=unit(1-d/(hud?8.f:12.f)),rim=u*u*(3-2*u);
            float bend=strength*square(slope)*(.62f+.38f*slope)*(1+.085f*rim),tint=1-square(slope);
            float fx=0,fy=0,nearest=1.e6f,controlDistance=1.e6f;
            for(const auto& z:zones){
                float dx=std::max({z.left-px,0.f,px-z.right}),dy=std::max({z.top-py,0.f,py-z.bottom});
                float distance=dx*dx+dy*dy;
                if(z.kind)controlDistance=std::min(controlDistance,distance);
                if(!z.kind)nearest=std::min(nearest,distance);
                else if(px>=z.left && px<=z.right && py>=z.top && py<=z.bottom){
                    float hw=(z.right-z.left)/2,hh=(z.bottom-z.top)/2,rr=std::min({z.kind==2?14.f:12.f,hw,hh});
                    float cx=px-z.left-hw,cy=py-z.top-hh;
                    float a=std::abs(cx)-hw+rr,b=std::abs(cy)-hh+rr,arc=std::hypot(std::max(0.f,a),std::max(0.f,b));
                    float depth=rr-arc-std::min(std::max(a,b),0.f),band=z.kind==2?4.5f:7.f;
                    if(depth<=0||depth>=band)continue;
                    float gain=square(1-depth/band)*std::min(1.f,depth*1.6f)*(z.kind==2?.62f:1.f);
                    fx=std::copysign(arc?std::max(0.f,a)/arc:float(a>b),cx)*gain;
                    fy=std::copysign(arc?std::max(0.f,b)/arc:float(b>=a),cy)*gain;
                }
            }
            float incidence=-.68f*nx-.73f*ny,front=std::max(0.f,incidence),back=std::max(0.f,-incidence);
            float lip=std::exp(-d/7),gloss=.83f*std::exp(-square(d-.9f)/2.3f)+.29f*std::exp(-d/6);
            float shine=std::clamp(gloss*(.12f+1.10f*std::pow(front,5.f)+.68f*std::pow(back,7.f))+.062f*front*front*front*lip*rim,0.f,.95f);
            if(surface==Surface::Adaptive){
                bend*=1.16f;
                shine=.78f*shine+.14f*std::exp(-square(d-2.8f)/1.6f)*back*back;
            }
            float facing=-.68f*fx-.73f*fy;
            float shade=lip*(.023f+.11f*square(1-std::abs(incidence)))+.292f*(.18f+.82f*back)*lip*(1-lip)*rim+.075f*std::max(0.f,-facing);
            shine+=(1-shine)*(.52f*std::max(0.f,facing)+.14f*std::max(0.f,-facing));
            map[y*w+x]={std::clamp(x-density*(nx*bend+xx*.024f*tint+10*fx),0.f,float(w-1)),
                         std::clamp(y-density*(ny*bend+yy*.024f*tint+10*fy),0.f,float(h-1)),
                         tint,rim,std::exp(-nearest/288.f),shine,shade,surface==Surface::Lustre?std::exp(-controlDistance/72.f):0.f};
        }
    }
    void blurPass(const Image& from,Image& to,int radius,bool vertical){
        int lines=vertical?w:h,count=vertical?h:w,step=vertical?w:1,n=radius*2+1;
        for(int line=0;line<lines;++line){
            int base=vertical?line:line*w,sb=0,sg=0,sr=0;
            auto get=[&](int k){return from[base+std::clamp(k,0,count-1)*step];};
            for(int k=-radius;k<=radius;++k){auto p=get(k);sb+=p.b;sg+=p.g;sr+=p.r;}
            for(int k=0;k<count;++k){
                to[base+k*step]={(unsigned char)(sb/n),(unsigned char)(sg/n),(unsigned char)(sr/n),255};
                auto old=get(k-radius),next=get(k+radius+1);sb+=next.b-old.b;sg+=next.g-old.g;sr+=next.r-old.r;
            }
        }
    }
    // Recover the underlying fill even when bright glyphs cover part of each tile.
    // Averaging first would let those glyphs change the polarity of the WPF controls.
    void sampleAmbient(){
        int span=std::max(1,(int)std::round(24*density));
        int cols=(w+span-1)/span,rows=(h+span-1)/span;
        tileSamples.resize(size_t(cols)*rows);
        for(int ty=0;ty<rows;++ty)for(int tx=0;tx<cols;++tx){
            struct Bin {int count{},b{},g{},r{};};
            Bin bins[32]{};
            int count=0;
            for(int y=ty*h/rows;y<(ty+1)*h/rows;y+=2)for(int x=tx*w/cols;x<(tx+1)*w/cols;x+=2){
                auto p=raw[y*w+x];
                auto& bin=bins[std::min(31,int(luma(p))/8)];
                ++count;++bin.count;bin.b+=p.b;bin.g+=p.g;bin.r+=p.r;
            }
            // On text, the fill is usually the most common tone. Use the median on gradients.
            const auto dominant=std::max_element(bins,bins+32,[](const Bin& a,const Bin& b){return a.count<b.count;});
            auto tone=dominant;
            if(dominant->count*3<count){
                int rank=count/2;
                for(auto& bin:bins){
                    if(rank<bin.count){tone=&bin;break;}
                    rank-=bin.count;
                }
            }
            tileSamples[ty*cols+tx]={(unsigned char)(tone->b/tone->count),(unsigned char)(tone->g/tone->count),(unsigned char)(tone->r/tone->count),255};
        }
        // A heavy glyph can fill one tile. Suppress isolated tones while retaining extended window boundaries.
        tiles=tileSamples;
        for(int ty=0;ty<rows;++ty)for(int tx=0;tx<cols;++tx){
            Pixel neighbors[8];int n=0;
            for(int y=std::max(0,ty-1);y<=std::min(rows-1,ty+1);++y)
                for(int x=std::max(0,tx-1);x<=std::min(cols-1,tx+1);++x)
                    if(x!=tx || y!=ty)neighbors[n++]=tileSamples[y*cols+x];
            if(n<3)continue;
            std::sort(neighbors,neighbors+n,[](Pixel a,Pixel b){return luma(a)<luma(b);});
            auto& p=tiles[ty*cols+tx];
            auto lo=neighbors[n/4],hi=neighbors[n-1-n/4];
            if(luma(p)>luma(hi)+48 || luma(p)<luma(lo)-48)p=neighbors[n/2];
        }
        ambient.resize(raw.size());
        for(int y=0;y<h;++y){
            float gy=std::clamp((y+.5f)*rows/h-.5f,0.f,float(rows-1));
            int top=(int)gy,bottom=std::min(top+1,rows-1);
            for(int x=0;x<w;++x){
                float gx=std::clamp((x+.5f)*cols/w-.5f,0.f,float(cols-1));
                int left=(int)gx,right=std::min(left+1,cols-1);
                auto a=tiles[top*cols+left],b=tiles[top*cols+right],c=tiles[bottom*cols+left],d=tiles[bottom*cols+right];
                auto interpolate=[&](int av,int bv,int cv,int dv){
                    return (unsigned char)std::round(std::lerp(std::lerp(float(av),float(bv),gx-left),std::lerp(float(cv),float(dv),gx-left),gy-top));
                };
                ambient[y*w+x]={interpolate(a.b,b.b,c.b,d.b),interpolate(a.g,b.g,c.g,d.g),interpolate(a.r,b.r,c.r,d.r),255};
            }
        }
    }
    void render(){
        int nearRadius=std::max(1,(int)std::round(2.2f*density)),farRadius=std::max(1,(int)std::round(6*density));
        blurPass(raw,scratch,nearRadius,false);blurPass(scratch,nearBlur,nearRadius,true);
        blurPass(nearBlur,scratch,farRadius,false);blurPass(scratch,farBlur,farRadius,true);
        const bool lustre=surface==Surface::Lustre;
        if(lustre)sampleAmbient();
        float luminance=0;int samples=0;
        for(size_t i=0;i<raw.size();i+=73){auto p=raw[i];luminance+=.2126f*p.r+.7152f*p.g+.0722f*p.b;++samples;}
        luminance/=samples;
        float dark=unit((123-luminance)/62),paper=unit((luminance-198)/34);
        for(size_t i=0;i<map.size();++i){
            const auto& m=map[i];int ix=(int)m.x,iy=(int)m.y,base=iy*w+ix;
            int nextX=std::min(1,w-1-ix),nextY=iy+1<h?w:0;
            float ax=m.x-ix,ay=m.y-iy;
            float diff=unit((.08f+(.025f+.065f*(paper+dark))*m.tint+(.31f+.53f*paper+.25f*dark)*m.ink)*(1-.18f*m.rim));
            if(surface==Surface::Adaptive)diff=(.18f+.09f*m.tint+.48f*m.ink)*(1-.35f*m.rim);
            const auto ground=lustre?ambient[base]:Pixel{};
            float night=lustre?nightWeight(luma(ground)):0.f;
            float protect=night*std::max(m.ink,m.control);
            if(lustre)diff*=1-.64f*night*(1-std::max(m.ink,m.control));
            float medium=2*diff*(1-diff),soft=diff*diff,sharp=1-medium-soft;
            auto p=farBlur[base];float local=.2126f*p.r+.7152f*p.g+.0722f*p.b;
            float milk;
            if(surface==Surface::Adaptive){
                milk=.012f+.016f*m.tint;
            }else{
                float veil=.035f+.455f*dark-.010f*paper+(.025f+.010f*dark)*m.tint;
                float light=local+(248-local)*veil,lack=185-light;
                float lift=(lack+std::sqrt(lack*lack+36))*.5f/std::max(35.f,248-light);
                milk=(veil+(1-veil)*(m.ink+.83f*dark*(1-m.ink))*std::min(.73f,lift))*(1-.115f*m.rim*(1-.75f*m.ink));
                if(surface==Surface::TonalLiquid || lustre){
                    float nightVeil=(lustre?.002f+.005f*m.tint:.012f+.034f*m.tint)*(1-.115f*m.rim);
                    milk+=(nightVeil-milk)*(lustre?night:nightWeight(local));
                }
            }
            auto channel=[&](const Image& buf,int k){
                const auto* a=reinterpret_cast<const unsigned char*>(&buf[base]);
                const auto* b=reinterpret_cast<const unsigned char*>(&buf[base+nextY]);
                return (a[k]+ax*(a[4*nextX+k]-a[k]))*(1-ay)+(b[k]+ax*(b[4*nextX+k]-b[k]))*ay;
            };
            unsigned char* dst=reinterpret_cast<unsigned char*>(&out[i]);
            for(int k=0;k<3;++k){
                float c=sharp*channel(raw,k)+medium*channel(nearBlur,k)+soft*channel(farBlur,k);
                c+=.045f*(c-local);
                if(protect>0){
                    float fill=reinterpret_cast<const unsigned char*>(&ground)[k];
                    float detail=std::clamp(channel(farBlur,k)-fill,-32.f,32.f);
                    c+=(fill+.20f*detail-c)*protect;
                }
                c=(c+((250-2*k)-c)*milk)*(1-m.shade);
                dst[k]=(unsigned char)std::clamp(c+(255-c)*m.reflection+.5f,0.f,255.f);
            }
            dst[3]=255;
        }
        if(surface!=Surface::Liquid){
            needsLightInk=false;
            // Lustre follows the background fill. Adaptive and TonalLiquid follow the refracted sample.
            maskW=(w+7)/8;maskH=(h+7)/8;inkMask.resize(size_t(maskW)*maskH);
            for(int y=0;y<maskH;++y)for(int x=0;x<maskW;++x){
                int px=(2*x+1)*w/(2*maskW),py=(2*y+1)*h/(2*maskH);
                const auto& optics=map[py*w+px];
                float level=lustre?luma(ambient[py*w+px]):luma(farBlur[int(optics.y)*w+int(optics.x)]);
                auto coverage=(unsigned char)std::round(255*unit((144-level)/50));
                inkMask[y*maskW+x]=coverage;
                needsLightInk|=coverage>119; // First non-zero light-ink weight in AdaptiveInk.hlsl.
            }
        }
    }
    int frame(RECT rect,float dpi,bool compact,Surface nextSurface,const Zone* bounds,int count){
        int sw=rect.right-rect.left,sh=rect.bottom-rect.top;
        if(sw<=0||sh<=0||sw>8192||sh>8192||dpi<=0||count<0||count>2048)return -1;
        float den=std::min(1.35f,dpi);
        int nw=std::max(1,(int)std::round(sw*den/dpi)),nh=std::max(1,(int)std::round(sh*den/dpi));
        if(size_t(nw)*nh>2000000)return -1;
        bool resize=w!=nw||h!=nh,move=sourceW!=sw||sourceH!=sh;
        bool rebuild=resize||den!=density||compact!=hud||nextSurface!=surface||count!=zones.size()||(count && std::memcmp(zones.data(),bounds,count*sizeof(Zone)));
        if(resize){
            w=nw;h=nh;size_t n=size_t(w)*h;
            for(auto buf:{&raw,&nearBlur,&farBlur,&scratch,&out})buf->resize(n);
            map.resize(n);
        }
        if(move){
            sourceW=sw;sourceH=sh;
            if(!SetWindowPos(host,nullptr,0,0,sw,sh,SWP_NOACTIVATE|SWP_NOZORDER))return -10;
            if(!SetWindowPos(child,nullptr,0,0,sw,sh,SWP_NOACTIVATE|SWP_NOZORDER))return -1;
        }
        if(rebuild){
            density=den;hud=compact;surface=nextSurface;zones.clear();if(count)zones.assign(bounds,bounds+count);configureMap();
        }
        changed=rebuild;received=false;
        if(!capture.usable())return -11;
        activeSample=this;
        BOOL ok=MagSetWindowSource(child,rect);
        activeSample=nullptr;
        if(!ok)return -11;
        if(!received)return reason;
        if(!changed)return 0;
        render();return 1;
    }
};
}
extern "C" __declspec(dllexport) void* LiquidCreate(HWND card,int w,int h) noexcept {
    try{auto s=std::make_unique<SamplingWindow>();if(s->open(card,w,h))return s.release();}catch(...){}
    return nullptr;
}
extern "C" __declspec(dllexport) int LiquidFrame(SamplingWindow* s,int x,int y,int w,int h,float dpi,int flags,const Zone* bounds,int count,void** pixels,int* rw,int* rh) noexcept{
    auto surface=(flags&16)?Surface::Lustre:(flags&8)?Surface::TonalLiquid:(flags&4)?Surface::Adaptive:Surface::Liquid;
    try{int result=s->frame({x,y,x+w,y+h},dpi,(flags&1)!=0,surface,bounds,count);*pixels=s->out.data();*rw=s->w;*rh=s->h;return result;}catch(...){return -1;}
}
extern "C" __declspec(dllexport) const unsigned char* LiquidInkMask(SamplingWindow* s,int* w,int* h) noexcept{
    *w=s->maskW;*h=s->maskH;
    return s->surface!=Surface::Adaptive && !s->needsLightInk ? nullptr : s->inkMask.data();
}
extern "C" __declspec(dllexport) void LiquidDestroy(SamplingWindow* s) noexcept{delete s;}
