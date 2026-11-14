cmake_minimum_required(VERSION 3.20)
project(CashVoiceAPO CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT so audiodg.exe doesn't need a VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

add_library(CashVoiceAPO SHARED src/CashVoiceAPO.cpp src/dsp.h src/exports.def)
target_compile_definitions(CashVoiceAPO PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(CashVoiceAPO PRIVATE /W3 /O2 /EHsc)
target_link_libraries(CashVoiceAPO PRIVATE ole32 uuid advapi32)
