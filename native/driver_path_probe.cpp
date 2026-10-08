// Passive filesystem discovery probe; never connects to or changes SteamVR.
#include <windows.h>
#include <sys/stat.h>
#include <cerrno>
#include <cstdio>
#include <iostream>
#include <string>
int wmain(int argc,wchar_t**argv){
    if(argc!=2){std::cerr<<"Usage: switcheroonie-path-probe <driver-directory>\n";return 3;}
    std::wstring path=argv[1];DWORD attributes=GetFileAttributesW(path.c_str());DWORD attributeError=attributes==INVALID_FILE_ATTRIBUTES?GetLastError():0;
    struct _stat first{};errno=0;int statResult=_wstat(path.c_str(),&first);int statError=errno;DWORD statWinError=GetLastError();
    struct _stat64 wide{};errno=0;int stat64Result=_wstat64(path.c_str(),&wide);int stat64Error=errno;DWORD stat64WinError=GetLastError();
    std::cout<<"{\"readOnly\":true,\"getFileAttributes\":"<<attributes<<",\"attributeWinError\":"<<attributeError<<",\"wstatResult\":"<<statResult<<",\"wstatErrno\":"<<statError<<",\"wstatLastWinError\":"<<statWinError<<",\"wstatMode\":"<<first.st_mode<<",\"wstat64Result\":"<<stat64Result<<",\"wstat64Errno\":"<<stat64Error<<",\"wstat64LastWinError\":"<<stat64WinError<<",\"wstat64Mode\":"<<wide.st_mode<<",\"directoryBit\":"<<_S_IFDIR<<",\"ctime\":"<<wide.st_ctime<<",\"mtime\":"<<wide.st_mtime<<",\"atime\":"<<wide.st_atime<<"}\n";
    return (attributes!=INVALID_FILE_ATTRIBUTES&&statResult==0&&(first.st_mode&_S_IFDIR)&&stat64Result==0)?0:2;
}
