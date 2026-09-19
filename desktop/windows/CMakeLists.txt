cmake_minimum_required(VERSION 3.24)
project(GitHubTeamAppDesktop LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()
add_executable(GitHubTeamAppPolicyTests policy_tests.cpp)
add_test(NAME DesktopPolicy COMMAND GitHubTeamAppPolicyTests)

if(NOT WIN32)
    return()
endif()
if(NOT MSVC)
    message(FATAL_ERROR "The Windows shell requires the Visual Studio C++ toolchain and Windows SDK.")
endif()

enable_language(RC)
include(FetchContent)
FetchContent_Declare(webview2
    URL "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3856.49/microsoft.web.webview2.1.0.3856.49.nupkg"
    DOWNLOAD_NAME "WebView2.zip"
    URL_HASH SHA256=bc0f76eb911b569838dc4aa8f8d325269b966bedb592863d26211aef3a099f1a
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(webview2)

if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
    set(webview2_arch arm64)
elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set(webview2_arch x64)
else()
    message(FATAL_ERROR "Supported Windows desktop architectures are x64 and ARM64.")
endif()

configure_file(app.rc "${CMAKE_CURRENT_BINARY_DIR}/app.rc" @ONLY)
add_custom_command(OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/app.ico"
    COMMAND powershell -NoProfile -ExecutionPolicy Bypass -File "${CMAKE_CURRENT_SOURCE_DIR}/create-icon.ps1"
            -Path "${CMAKE_CURRENT_BINARY_DIR}/app.ico"
    DEPENDS create-icon.ps1
    VERBATIM)
add_custom_target(AppIcon DEPENDS "${CMAKE_CURRENT_BINARY_DIR}/app.ico")
add_executable(GitHubTeamApp WIN32 main.cpp "${CMAKE_CURRENT_BINARY_DIR}/app.rc")
add_dependencies(GitHubTeamApp AppIcon)
set_source_files_properties("${CMAKE_CURRENT_BINARY_DIR}/app.rc" PROPERTIES
    OBJECT_DEPENDS "${CMAKE_CURRENT_BINARY_DIR}/app.ico")
set_target_properties(GitHubTeamApp PROPERTIES
    OUTPUT_NAME "GitHub Team App"
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_compile_definitions(GitHubTeamApp PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX WINVER=0x0A00 _WIN32_WINNT=0x0A00)
target_compile_options(GitHubTeamApp PRIVATE /W4 /permissive- /utf-8)
target_link_options(GitHubTeamApp PRIVATE /MANIFEST:EMBED
    "/MANIFESTINPUT:${CMAKE_CURRENT_SOURCE_DIR}/app.manifest")
set_property(TARGET GitHubTeamApp APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/app.manifest")
target_include_directories(GitHubTeamApp PRIVATE "${webview2_SOURCE_DIR}/build/native/include")
target_link_libraries(GitHubTeamApp PRIVATE
    "${webview2_SOURCE_DIR}/build/native/${webview2_arch}/WebView2LoaderStatic.lib"
    user32 gdi32 ole32 oleaut32 shell32 shlwapi version)
install(TARGETS GitHubTeamApp RUNTIME DESTINATION .)
install(FILES "${webview2_SOURCE_DIR}/LICENSE.txt" DESTINATION . RENAME WebView2.LICENSE.txt)
install(FILES "${webview2_SOURCE_DIR}/NOTICE.txt" DESTINATION . RENAME WebView2.NOTICE.txt)
