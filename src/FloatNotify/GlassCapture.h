#pragma once
#include <windows.h>
#include <magnification.h>
#include <algorithm>
#include <vector>

// All capture windows live on the resident UI thread. Exclude peer glass from every
// sampler so two overlapping cards cannot keep repainting each other's reflections.
class GlassCapture {
    inline static std::vector<GlassCapture*> attached;
    inline static std::vector<HWND> filter;
    inline static unsigned users{};
    HWND target{}, face{}, plate{}, frame{};
    bool initialized{}, filtered{};
    static void refresh(){
        filter.clear();
        for(auto p:attached){
            filter.push_back(p->face);
            if(p->frame)filter.push_back(p->frame);
            if(p->plate)filter.push_back(p->plate);
        }
        for(auto p:attached)
            p->filtered=MagSetWindowFilterList(p->target,MW_FILTERMODE_EXCLUDE,static_cast<int>(filter.size()),filter.data())!=FALSE;
    }
public:
    GlassCapture(){
        initialized=users!=0 || MagInitialize()!=FALSE;
        if(initialized)++users;
    }
    GlassCapture(const GlassCapture&)=delete;
    GlassCapture& operator=(const GlassCapture&)=delete;
    bool ready() const {return initialized;}
    bool usable() const {return filtered;}
    bool attach(HWND magnifier,HWND card,HWND host,HWND backdrop=nullptr){
        filter.reserve((attached.size()+1)*3);
        attached.push_back(this);
        target=magnifier;face=card;frame=host;plate=backdrop;
        refresh();
        return filtered;
    }
    void detach() noexcept {
        if(!target)return;
        std::erase(attached,this);
        target=nullptr;
        refresh();
    }
    ~GlassCapture(){detach();if(initialized && --users==0)MagUninitialize();}
};

