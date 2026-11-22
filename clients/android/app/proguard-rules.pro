# WireGuard 的 Go 后端通过 JNI 调用，保留其类名。
-keep class com.wireguard.** { *; }
-keepclassmembers class com.wireguard.** { *; }
