// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "PolicyTests.h"
#include <iostream>

int main()
{
    try
    {
        RunPolicyTests();
        std::cout << "Native desktop URL, process ID, and argument quoting tests passed.\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
