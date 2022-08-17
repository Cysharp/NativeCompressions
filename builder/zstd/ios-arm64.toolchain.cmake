set(CMAKE_VERBOSE_MAKEFILE ON)
set(CMAKE_COLOR_MAKEFILE   ON)

set(BUILD_SHARED_LIBS ON)

set(CMAKE_BUILD_TYPE  Release)

set(CMAKE_SYSTEM_NAME      Darwin)
set(CMAKE_SYSTEM_VERSION   8.0)
set(CMAKE_SYSTEM_PROCESSOR arm64)

set(CMAKE_ASM_COMPILER /Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/clang)
set(CMAKE_ASM_FLAGS "-arch arm64")

set(CMAKE_C_COMPILER "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/clang")
set(CMAKE_C_FLAGS "-isysroot /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk -arch arm64 -miphoneos-version-min=8.0 -Qunused-arguments -Os -pipe -Wl,-S -Os -DNDEBUG -isysroot /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk -Qunused-arguments -D__arm64__ -isysroot /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk -arch arm64 -miphoneos-version-min=8.0 -Wl,-S")

set(CMAKE_CXX_COMPILER "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/clang++")
set(CMAKE_CXX_FLAGS "-isysroot /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk -arch arm64 -miphoneos-version-min=8.0 -Qunused-arguments -Os -pipe -Wl,-S -Os -DNDEBUG -isysroot /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk -Qunused-arguments -D__arm64__ -isysroot /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk -arch arm64 -miphoneos-version-min=8.0 -Wl,-S")

set(CMAKE_AR     "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/ar")
set(CMAKE_NM     "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/nm")
set(CMAKE_RANLIB "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/ranlib")
set(CMAKE_STRIP  "/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/strip")

set(CMAKE_OSX_SYSROOT "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk")

set(CMAKE_OSX_ARCHITECTURES "arm64" CACHE STRING "")

set(CMAKE_FIND_DEBUG_MODE OFF)

set(CMAKE_LIBRARY_PATH "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk/usr/lib")
