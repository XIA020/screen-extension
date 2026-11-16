package com.limelight;

import android.content.Context;
import android.os.Build;
import android.provider.Settings;

import java.util.UUID;

final class DisplayDeviceIdentity {
    private DisplayDeviceIdentity() {}

    static synchronized String id(Context context) {
        android.content.SharedPreferences prefs = context.getSharedPreferences("display_device", Context.MODE_PRIVATE);
        String id = prefs.getString("id", null);
        if (id == null) {
            id = UUID.randomUUID().toString();
            prefs.edit().putString("id", id).apply();
        }
        return id;
    }

    static String name(Context context) {
        String name = null;
        try {
            name = Settings.Global.getString(context.getContentResolver(), "device_name");
        } catch (SecurityException ignored) {
        }
        if (name == null || name.trim().isEmpty()) name = Build.MODEL;
        name = name.replaceAll("[\\p{Cc}]", " ").trim();
        if (name.isEmpty()) return "安卓设备";
        return name.substring(0, Math.min(name.length(), 48));
    }
}
