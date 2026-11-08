// Match the allocator ABI used by Facepunch/Valve's Qt build.
// Reference: Facepunch/qt, valve/qtbase/src/valve_memoverride/valve_memoverride.cpp.
#define NOMINMAX
#include <Windows.h>
#include <cstdlib>
#include <new>

#pragma warning(disable:4073)
#pragma init_seg(lib)
namespace {
using Alloc=void*(*)(size_t);
using Free=void(*)(void*);
using Realloc=void*(*)(void*,size_t);
Alloc allocate=std::malloc;
Free release=std::free;
Realloc reallocate=std::realloc;
using GetAllocators=void(*)(Alloc*,Free*,Realloc*);
int install(){
    auto module=GetModuleHandleW(L"tier0.dll");
    auto get=module?reinterpret_cast<GetAllocators>(GetProcAddress(module,"Qt_GetCustomAllocators")):nullptr;
    if(get)get(&allocate,&release,&reallocate);
    return 1;
}
int installed=install();
}
void* operator new(size_t size){if(auto p=allocate(size?size:1))return p;throw std::bad_alloc();}
void* operator new[](size_t size){return ::operator new(size);}
void operator delete(void* p)noexcept{release(p);}
void operator delete[](void* p)noexcept{release(p);}
void operator delete(void* p,size_t)noexcept{release(p);}
void operator delete[](void* p,size_t)noexcept{release(p);}
