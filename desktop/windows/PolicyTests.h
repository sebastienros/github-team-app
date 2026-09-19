// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma once

#include "Policy.h"
#include <stdexcept>

inline void RunPolicyTests()
{
    const auto require = [](bool condition) {
        if (!condition) throw std::runtime_error("Native desktop policy assertion failed.");
    };
    require(desktop::LoopbackOrigin(L"http://127.0.0.1:5143/") == L"http://127.0.0.1:5143");
    require(desktop::LoopbackOrigin(L"http://127.0.0.1:65535") == L"http://127.0.0.1:65535");
    for (const auto invalid : {
        L"https://127.0.0.1:5143", L"http://localhost:5143", L"http://evil.test:5143",
        L"http://127.0.0.1:0", L"http://127.0.0.1:65536", L"http://127.0.0.1:123456",
        L"http://user@127.0.0.1:5143", L"http://127.0.0.1:5143/?x=1",
        L"http://127.0.0.1:5143/#x", L"http://127.0.0.1:5143/other",
        L"http://127.0.0.1:5143.evil.test", L"http://127.0.0.1:", L"http://127.0.0.1:-1"
    }) require(!desktop::LoopbackOrigin(invalid));
    constexpr auto origin = L"http://127.0.0.1:5143";
    for (const auto local : { origin, L"http://127.0.0.1:5143/", L"http://127.0.0.1:5143/api/state",
                             L"http://127.0.0.1:5143/?repo=test", L"http://127.0.0.1:5143#hash" })
        require(desktop::SameOrigin(local, origin));
    for (const auto invalid : { L"http://127.0.0.1:51430", L"http://127.0.0.1:5143.evil.test",
                               L"http://127.0.0.1:5143@evil.test", L"https://127.0.0.1:5143",
                               L"http://127.0.0.1:5143\\@evil.test" })
        require(!desktop::SameOrigin(invalid, origin));
    for (const auto external : { L"https://github.com/owner/repo", L"https://enterprise.test/owner/repo",
                                L"ghapp://session/new", L"ghapp://session/new?repo=owner/repo" })
        require(desktop::ExternalUrl(external));
    for (const auto invalid : { L"file:///C:/secret", L"javascript:alert(1)", L"http://evil.test",
                               L"ghapp://session/newer", L"ghapp://session/new/other", L"ghapp://other/new",
                               L"https://user@github.com", L"https://", L"https://%65vil.test",
                               L"https://github.com\\evil.test", L"https://github.com/\nother" })
        require(!desktop::ExternalUrl(invalid));
    require(desktop::ProcessId(L"1234") == 1234);
    require(desktop::ProcessId(L"4294967295") == 0xffffffffUL);
    for (const auto invalid : { L"", L"0", L"1", L"-1", L"12junk", L"4294967296", L"99999999999" })
        require(!desktop::ProcessId(invalid));
    require(desktop::QuoteArgument(L"") == L"\"\"");
    require(desktop::QuoteArgument(L"C:\\app path\\host.exe") == L"\"C:\\app path\\host.exe\"");
    require(desktop::QuoteArgument(L"folder\\") == L"\"folder\\\\\"");
    require(desktop::QuoteArgument(L"a\"b") == L"\"a\\\"b\"");
    require(desktop::QuoteArgument(L"a\\\"b") == L"\"a\\\\\\\"b\"");
    require(desktop::QuoteArgument(L"hello & start evil") == L"\"hello & start evil\"");
}
