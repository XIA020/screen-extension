package com.limelight;

import android.app.AlertDialog;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.util.Base64;
import android.widget.Toast;

import com.limelight.computers.ComputerManagerService;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.PairingManager.PairState;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import android.os.SystemClock;
import java.math.BigInteger;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.MessageDigest;
import java.security.Signature;
import java.security.spec.RSAPublicKeySpec;
import java.util.Arrays;
import java.util.UUID;


final class DisplayInvitation {
    interface BinderProvider {
        ComputerManagerService.ComputerManagerBinder get();
    }

    private static final int PORT = 47995;
    private static final String PROTOCOL = "TabletDisplay/1";
    private static final String DISCOVERY = "TabletDisplay/2";
    private static final long DISCOVERY_TTL_MS = 60_000;
    private final PcView activity;
    private final BinderProvider binderSupplier;
    private final Handler main = new Handler(Looper.getMainLooper());
    private DatagramSocket socket;
    private AlertDialog dialog;
    private String lastInvitation;

    DisplayInvitation(PcView activity,
                      BinderProvider binderSupplier) {
        this.activity = activity;
        this.binderSupplier = binderSupplier;
    }

    void start() {
        try {
            DatagramSocket listener = new DatagramSocket(PORT);
            socket = listener;
            new Thread(() -> receive(listener), "display-invitations").start();
        } catch (IOException e) {
            toast("无法监听副屏邀请");
        }
    }

    void stop() {
        if (socket != null) {
            socket.close();
            socket = null;
        }
        if (dialog != null) {
            dialog.dismiss();
        }
    }

    private void receive(DatagramSocket listener) {
        byte[] buffer = new byte[1200];
        String seenNonce = null;
        InetAddress seenHost = null;
        int seenPort = 0;
        String challenge = null;
        long seenAt = 0;
        while (!listener.isClosed()) {
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            try {
                listener.receive(packet);
            } catch (IOException e) {
                return;
            }
            String[] message = new String(packet.getData(), packet.getOffset(), packet.getLength(),
                    Charset.forName("UTF-8")).split("\n", -1);
            if (message.length == 3 && PROTOCOL.equals(message[0])) {
                InetAddress source = packet.getAddress();
                main.post(() -> show(listener, source, message[1], message[2]));
            }
            else if ((message.length == 3 || message.length == 4 && "proof".equals(message[3]))
                    && DISCOVERY.equals(message[0]) && "scan".equals(message[1]) && validId(message[2])) {
                seenNonce = message[2];
                seenHost = packet.getAddress();
                seenPort = packet.getPort();
                seenAt = SystemClock.elapsedRealtime();
                challenge = message.length == 4 ? UUID.randomUUID().toString() : null;
                String reply = DISCOVERY + "\nfound\n" + seenNonce + "\n" +
                        DisplayDeviceIdentity.id(activity) + "\n" + DisplayDeviceIdentity.name(activity) +
                        (challenge == null ? "" : "\n" + challenge);
                byte[] bytes = reply.getBytes(StandardCharsets.UTF_8);
                try {
                    listener.send(new DatagramPacket(bytes, bytes.length, seenHost, packet.getPort()));
                } catch (IOException ignored) {
                }
            }
            else if ((message.length == 6 || message.length == 10) && DISCOVERY.equals(message[0]) && "connect".equals(message[1])
                    && message[2].equals(seenNonce) && packet.getAddress().equals(seenHost) && packet.getPort() == seenPort
                    && SystemClock.elapsedRealtime() - seenAt <= DISCOVERY_TTL_MS
                    && message[3].equals(DisplayDeviceIdentity.id(activity)) && validId(message[4])) {
                if (!"47989".equals(message[5]) && !"49989".equals(message[5])) continue;
                if (message.length == 10 && (!message[6].equals(challenge) || !verifyInvitation(message))) continue;
                String key = message.length == 10 ? message[7] + ":" + message[8] : null;
                seenNonce = null;
                int hostPort = Integer.parseInt(message[5]);
                InetAddress source = packet.getAddress();
                main.post(() -> showDiscoveryConnection(listener, source, message[4], hostPort, message[2], key));
            }
        }
    }

    private static boolean validId(String value) {
        try {
            return value.length() == 36 && UUID.fromString(value).toString().equalsIgnoreCase(value);
        } catch (IllegalArgumentException ignored) {
            return false;
        }
    }

    private static boolean verifyInvitation(String[] message) {
        try {
            byte[] modulus = Base64.decode(message[7], Base64.NO_WRAP);
            byte[] exponent = Base64.decode(message[8], Base64.NO_WRAP);
            byte[] proof = Base64.decode(message[9], Base64.NO_WRAP);
            if (modulus.length != 256 || exponent.length != 3 || proof.length != 256) return false;
            Signature signature = Signature.getInstance("SHA256withRSA");
            signature.initVerify(KeyFactory.getInstance("RSA").generatePublic(
                    new RSAPublicKeySpec(new BigInteger(1, modulus), new BigInteger(1, exponent))));
            signature.update(TextUtils.join("\n", Arrays.asList(message).subList(0, 9)).getBytes(StandardCharsets.UTF_8));
            return signature.verify(proof);
        } catch (GeneralSecurityException | IllegalArgumentException ignored) {
            return false;
        }
    }

    private String trustId(ComputerDetails computer) {
        try {
            if (computer == null || computer.serverCert == null) return null;
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(computer.serverCert.getEncoded());
            return computer.uuid + ":" + Base64.encodeToString(hash, Base64.NO_WRAP);
        } catch (GeneralSecurityException ignored) {
            return null;
        }
    }

    void remember(ComputerDetails computer, String key) {
        String id = trustId(computer);
        if (id != null && key != null) activity.getSharedPreferences("display_invitation", 0)
                .edit().putString(id, key).apply();
    }

    private void showDiscoveryConnection(DatagramSocket listener, InetAddress source, String hostId,
                                         int hostPort, String invitation, String key) {
        ComputerManagerService.ComputerManagerBinder binder = binderSupplier.get();
        if (socket != listener || binder == null || dialog != null || invitation.equals(lastInvitation)) return;
        lastInvitation = invitation;
        ComputerDetails computer = binder.getComputer(hostId);
        String id = trustId(computer);
        if (key != null && id != null && key.equals(activity.getSharedPreferences("display_invitation", 0)
                .getString(id, null))) {
            activity.reconnectDiscoveredHost(computer, source.getHostAddress(), hostPort);
            return;
        }
        dialog = new AlertDialog.Builder(activity)
                .setTitle("副屏连接")
                .setMessage("允许附近的电脑连接这台设备作为副屏？")
                .setNegativeButton("忽略", null)
                .setPositiveButton("连接", (ignored, which) ->
                        activity.acceptDiscoveredHost(source.getHostAddress(), hostPort, hostId, key))
                .create();
        dialog.setOnDismissListener(ignored -> dialog = null);
        dialog.show();
    }

    private void show(DatagramSocket listener, InetAddress source, String hostId, String invitation) {
        ComputerManagerService.ComputerManagerBinder binder = binderSupplier.get();
        if (socket != listener || binder == null || dialog != null || invitation.equals(lastInvitation)) {
            return;
        }
        try {
            UUID.fromString(invitation);
            UUID.fromString(hostId);
        } catch (IllegalArgumentException e) {
            return;
        }
        ComputerDetails computer = binder.getComputer(hostId);
        if (computer == null || computer.pairState != PairState.PAIRED || computer.serverCert == null
                || computer.activeAddress == null
                || !source.getHostAddress().equals(computer.activeAddress.address)) {
            return;
        }

        lastInvitation = invitation;
        dialog = new AlertDialog.Builder(activity)
                .setTitle("副屏连接邀请")
                .setMessage(computer.name + " 想将这台平板用作副屏。")
                .setNegativeButton("忽略", null)
                .setPositiveButton("连接", (ignored, which) -> activity.connectDesktop(computer, false))
                .create();
        dialog.setOnDismissListener(ignored -> dialog = null);
        dialog.show();
    }

    private void toast(String message) {
        Toast.makeText(activity, message, Toast.LENGTH_LONG).show();
    }
}
