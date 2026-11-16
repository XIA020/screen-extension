package com.limelight;

import android.content.Context;
import java.nio.charset.StandardCharsets;
import android.graphics.ImageFormat;
import android.graphics.Rect;
import android.graphics.SurfaceTexture;
import android.graphics.YuvImage;
import android.hardware.Camera;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;

import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.List;
import com.limelight.preferences.PreferenceConfiguration;

/** Sends a small, foreground-only camera preview over the same local link as the display. */
@SuppressWarnings("deprecation")
final class CameraReturn {
    private static final int PORT = 47996;
    private final HandlerThread thread = new HandlerThread("camera-return");
    private volatile Socket socket;
    private final String host;
    private final String deviceName;
    private final int slot;
    private final Context context;
    private final PreferenceConfiguration activeMode;
    private final java.util.function.IntConsumer onCommand;
    private final java.util.function.IntConsumer onDisplaySelection;
    private final java.util.function.Consumer<Boolean> onTrackpadMode;
    private final java.util.function.Consumer<Boolean> onError;
    private boolean attached;
    private Handler worker;
    private volatile boolean stopped;
    private Camera camera;
    private SurfaceTexture texture;
    private DataOutputStream output;
    private long lastFrame;
    private int cameraId = findCamera(Camera.CameraInfo.CAMERA_FACING_FRONT, 0);

    CameraReturn(Context context, String host, int slot, PreferenceConfiguration activeMode,
                 java.util.function.IntConsumer onCommand, java.util.function.IntConsumer onDisplaySelection,
                 java.util.function.Consumer<Boolean> onTrackpadMode,
                 java.util.function.Consumer<Boolean> onError) {
        this.host = host;
        this.slot = slot;
        this.context = context.getApplicationContext();
        this.activeMode = activeMode;
        deviceName = DisplayDeviceIdentity.name(context);
        this.onCommand = onCommand;
        this.onDisplaySelection = onDisplaySelection;
        this.onTrackpadMode = onTrackpadMode;
        this.onError = onError;
    }

    static boolean canSwitch() {
        return findCamera(Camera.CameraInfo.CAMERA_FACING_FRONT, -1) >= 0
                && findCamera(Camera.CameraInfo.CAMERA_FACING_BACK, -1) >= 0;
    }

    void setEnabled(boolean enabled) {
        worker.post(() -> {
            if (stopped) return;
            try {
                if (enabled && camera == null) startPreview();
                if (!enabled) {
                    releaseCamera();
                    output.writeInt(0);
                }
            } catch (IOException | RuntimeException e) {
                fail();
            }
        });
    }

    void pauseCapture() {
        worker.post(() -> {
            if (stopped || camera == null) return;
            try {
                releaseCamera();
                output.writeInt(-3);
            } catch (IOException | RuntimeException e) {
                fail();
            }
        });
    }

    void permissionDenied() {
        worker.post(() -> {
            try {
                output.writeInt(-1);
            } catch (IOException e) {
                fail();
            }
        });
    }

    void switchCamera() {
        worker.post(() -> {
            if (stopped) {
                return;
            }
            try {
                if (camera == null) return;
                Camera.CameraInfo info = new Camera.CameraInfo();
                Camera.getCameraInfo(cameraId, info);
                int facing = info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT
                        ? Camera.CameraInfo.CAMERA_FACING_BACK : Camera.CameraInfo.CAMERA_FACING_FRONT;
                int nextCamera = findCamera(facing, cameraId);
                if (nextCamera != cameraId) {
                    releaseCamera();
                    cameraId = nextCamera;
                    startPreview();
                }
            } catch (IOException | RuntimeException e) {
                fail();
            }
        });
    }

    private static int findCamera(int facing, int fallback) {
        Camera.CameraInfo info = new Camera.CameraInfo();
        for (int index = 0; index < Camera.getNumberOfCameras(); index++) {
            Camera.getCameraInfo(index, info);
            if (info.facing == facing) {
                return index;
            }
        }
        return fallback;
    }

    void start() {
        thread.start();
        worker = new Handler(thread.getLooper());
        worker.post(this::open);
    }

    void stop() {
        stopped = true;
        closeSocket();
        worker.post(() -> {
            try {
                releaseCamera();
            } finally {
                thread.quit();
            }
        });
    }

    private void closeSocket() {
        Socket connection = socket;
        if (connection == null) return;
        try {
            connection.close();
        } catch (IOException ignored) {
        }
    }

    private void open() {
        try {
            int oldestVersion = slot == 1 ? 5 : 6;
            for (int version = 8; version >= oldestVersion; version--) {
                try {
                    openSocket(version);
                    break;
                } catch (IOException failure) {
                    if (stopped || version == oldestVersion) throw failure;
                    closeSocket();
                }
            }
            if (stopped) return;
            attached = true;
            worker.post(heartbeat);
            new Thread(this::readCommands, "camera-control").start();
        } catch (IOException | RuntimeException e) {
            fail();
        }
    }

    private void openSocket(int version) throws IOException {
        Socket connection = new Socket();
        socket = connection;
        if (stopped) {
            connection.close();
            throw new IOException("Display control stopped");
        }
        connection.connect(new InetSocketAddress(host, PORT), 2500);
        output = new DataOutputStream(connection.getOutputStream());
        byte[] name = deviceName.getBytes(StandardCharsets.UTF_8);
        boolean modern = version >= 6;
        output.writeInt(0x54444330 + version);
        if (modern) {
            output.writeByte(slot);
            output.write(DisplayDeviceIdentity.id(context).getBytes(StandardCharsets.US_ASCII));
        }
        output.writeInt(name.length);
        output.write(name);
        output.writeBoolean(canSwitch());
        writeMode(activeMode);
        PreferenceConfiguration savedMode = PreferenceConfiguration.readPreferences(context);
        writeMode(savedMode);
        output.writeBoolean(savedMode.touchscreenTrackpad);
        if (modern) {
            output.writeBoolean(activeMode.playDeviceAudio);
            output.writeBoolean(savedMode.playDeviceAudio);
            connection.setSoTimeout(1500);
            if (connection.getInputStream().read() != 0x7e) throw new IOException("Display control protocol rejected");
            connection.setSoTimeout(0);
        }
    }

    private final Runnable heartbeat = new Runnable() {
        @Override
        public void run() {
            if (stopped) return;
            try {
                output.writeInt(-2);
                worker.postDelayed(this, 1800);
            } catch (IOException e) {
                fail();
            }
        }
    };

    private void readCommands() {
        try {
            DataInputStream input = new DataInputStream(socket.getInputStream());
            for (int command; !stopped && (command = input.read()) != -1;) {
                if (command == 4) {
                    int width = input.readInt();
                    int height = input.readInt();
                    int fps = input.readInt();
                    worker.post(() -> saveStreamMode(width, height, fps));
                } else if (command == 5) {
                    boolean trackpad = input.readBoolean();
                    worker.post(() -> saveTrackpadMode(trackpad));
                } else if (command == 6) {
                    boolean enabled = input.readBoolean();
                    worker.post(() -> {
                        if (stopped || !PreferenceConfiguration.saveDeviceAudio(context, enabled)) return;
                        try {
                            output.writeInt(-6);
                            output.writeBoolean(enabled);
                        } catch (IOException e) {
                            fail();
                        }
                    });
                } else if (command == 8) {
                    onDisplaySelection.accept(input.readUnsignedByte());
                } else if (command <= 3 || command == 7) {
                    onCommand.accept(command);
                }
            }
        } catch (IOException ignored) {
        }
        fail();
    }

    private void saveStreamMode(int width, int height, int fps) {
        if (stopped || !PreferenceConfiguration.saveDesktopStreamMode(context, width, height, fps)) return;
        try {
            output.writeInt(-4);
            output.writeInt(width);
            output.writeInt(height);
            output.writeInt(fps);
        } catch (IOException e) {
            fail();
        }
    }

    private void writeMode(PreferenceConfiguration mode) throws IOException {
        output.writeInt(mode.width);
        output.writeInt(mode.height);
        output.writeInt(mode.fps);
    }

    private void saveTrackpadMode(boolean trackpad) {
        if (stopped || !PreferenceConfiguration.saveTouchscreenTrackpad(context, trackpad)) return;
        onTrackpadMode.accept(trackpad);
        try {
            output.writeInt(-5);
            output.writeBoolean(trackpad);
        } catch (IOException e) {
            fail();
        }
    }

    private void releaseCamera() {
        try {
            if (camera != null) {
                camera.release();
                camera = null;
            }
        } finally {
            if (texture != null) {
                texture.release();
                texture = null;
            }
        }
    }

    private void startPreview() throws IOException {
        camera = Camera.open(cameraId);
        camera.setErrorCallback((error, source) -> fail());
        Camera.Parameters parameters = camera.getParameters();
        List<Camera.Size> sizes = parameters.getSupportedPreviewSizes();
        Camera.Size size = parameters.getPreviewSize();
        for (Camera.Size candidate : sizes) {
            if (Math.abs(candidate.width * candidate.height - 640 * 480)
                    < Math.abs(size.width * size.height - 640 * 480)) {
                size = candidate;
            }
        }
        parameters.setPreviewSize(size.width, size.height);
        parameters.setPreviewFormat(ImageFormat.NV21);
        camera.setParameters(parameters);
        texture = new SurfaceTexture(0);
        camera.setPreviewTexture(texture);
        final int width = size.width;
        final int height = size.height;
        camera.setPreviewCallbackWithBuffer((data, source) -> {
            if (stopped || source != camera || data == null) {
                return;
            }
            try {
                sendFrame(data, width, height);
            } catch (IOException | RuntimeException e) {
                fail();
            } finally {
                if (!stopped) {
                    source.addCallbackBuffer(data);
                }
            }
        });
        camera.addCallbackBuffer(new byte[width * height * 3 / 2]);
        camera.startPreview();
    }

    private void sendFrame(byte[] data, int width, int height) throws IOException {
        long now = SystemClock.elapsedRealtime();
        if (now - lastFrame < 100) {
            return;
        }
        lastFrame = now;
        ByteArrayOutputStream jpeg = new ByteArrayOutputStream();
        YuvImage image = new YuvImage(data, ImageFormat.NV21, width, height, null);
        if (image.compressToJpeg(new Rect(0, 0, width, height), 65, jpeg) && !stopped) {
            output.writeInt(jpeg.size());
            jpeg.writeTo(output);
        }
    }

    private void fail() {
        if (!stopped) {
            onError.accept(attached);
        }
    }
}

