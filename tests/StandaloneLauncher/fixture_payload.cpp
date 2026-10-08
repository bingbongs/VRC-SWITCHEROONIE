#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    std::vector<wchar_t> path(32768);
    const auto length=GetModuleFileNameW(nullptr,path.data(),DWORD(path.size()));
    if(!length || length>=path.size()) return 1;
    std::wstring output(path.data(),length);
    const auto separator=output.find_last_of(L"\\/");
    if(separator==std::wstring::npos) return 1;
    output.resize(separator+1); output+=L"fixture-result.txt";
    int count{};auto **arguments=CommandLineToArgvW(GetCommandLineW(),&count);
    if(!arguments) return 1;
    std::wstring record;
    for(int index=1;index<count;++index) { if(index>1) record+=L"|";record+=arguments[index]; }
    LocalFree(arguments);
    auto file=CreateFileW(output.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE) return 1;
    const wchar_t bom=0xFEFF;DWORD written{};
    bool ok=WriteFile(file,&bom,sizeof(bom),&written,nullptr)&&written==sizeof(bom);
    const auto bytes=DWORD(record.size()*sizeof(wchar_t));
    ok=ok&&WriteFile(file,record.data(),bytes,&written,nullptr)&&written==bytes;
    CloseHandle(file);
    // Only this harmless fixture reads a marker outside App. It models a
    // bootstrap that is still about to load dependencies after CreateProcess.
    auto marker=output.substr(0,separator);const auto parent=marker.find_last_of(L"\\/");
    if(parent!=std::wstring::npos)
    {
        marker.resize(parent+1);marker+=L"fixture-hold.flag";
        const auto until=GetTickCount64()+4000;
        while(GetFileAttributesW(marker.c_str())!=INVALID_FILE_ATTRIBUTES&&GetTickCount64()<until)Sleep(10);
    }
    return ok?0:1;
}
