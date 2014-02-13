## Localytics for Xamarin.Mac

This is a port for Xamarin.Mac of the Localytics Windows 8 C# SDK. Since the original SDK included components only available in the Windows Runtime API for Windows Store apps, modifications were needed in order to support a Xamarin.Mac project.

## Changes

The following were the changes made to the original SDK.

1. Replace Windows Runtime's `StorageFolder` with the more generic `DirectoryInfo` and `FileInfo`.

2. Change `libraryVersion` to a Xamarin-appropriate value of `xam_mac_1.0`.

3. Change implementation of `GetAppVersion()` to retrieve the bundle version via the `CFBundleShortVersionString` Core Foundation key.

4. Change a few blob strings in `GetBlobHeader()`:
	* Device Platform from `Windows 8` to `Mac OS X`
	* Device Model from `Windows Store Device` to `Mac Device`
	* Device OS Version from Window Runtime call to `NSProcessInfo.ProcessInfo.OperatingSystemVersionString`

5. Change implementation of `GetDeviceInfo()` to remove Windows Runtime call and replace with `Guid` + `NSUserDefaults` combination.

## Original License

Copyright (c) 2009, Char Software, Inc. d/b/a Localytics
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
    * Redistributions of source code must retain the above copyright
      notice, this list of conditions and the following disclaimer.
    * Neither the name of Char Software, Inc., Localytics nor the names of its 
      contributors may be used to endorse or promote products derived from this
      software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY CHAR SOFTWARE, INC. D/B/A LOCALYTICS ''AS IS'' AND 
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
DISCLAIMED. IN NO EVENT SHALL CHAR SOFTWARE, INC. D/B/A LOCALYTICS BE LIABLE 
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL 
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER 
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.