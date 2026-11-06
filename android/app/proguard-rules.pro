# JNI methods are looked up by name from libcordmic_native.so.
-keepclasseswithmembernames class * {
    native <methods>;
}

# The foreground service is started by class name from the manifest and from MainActivity.
-keep class dev.cordmic.MicService { *; }
