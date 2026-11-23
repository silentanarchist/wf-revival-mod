# Signs WF Revival (formerly Warpforge Revival) mod builds with the project owner's key (mod 0.14.0 and later install only signed updates).
# Folder layout (C:\modding\warpforge revival\mod-signing):
#   keys\       mod-signing-key.txt (never share it), public-keys.txt, make signing keys.bat
#   to-sign\    Claude puts the builds that are ready to publish here
#   signed\     the signed builds land here: install them with "2 - install signed mods.bat",
#               publish them with "3 - push signed mods to server.bat"
#
#   sign-mod.ps1            signs every DLL in to-sign\ into signed\ (run by "1 - sign builds.bat")
#   sign-mod.ps1 -Check     checks every DLL in signed\ carries a good signature (needs only public-keys.txt);
#                           exit code 1 when one does not (the push and install scripts stop then)
#   -Tester                 the same with to-sign-test\ and signed-test\: the testers' builds (0.15.32.1, .2, ...),
#                           pushed to W:\mod-test by "5 - push signed tester builds to server.bat"
# The signature goes on the end of the file (see ModSignature.cs in the mod for the format); nothing else changes.
param([switch]$Check, [switch]$Tester)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$keys = Join-Path $here 'keys'
$toSign = Join-Path $here $(if ($Tester) { 'to-sign-test' } else { 'to-sign' })
$signed = Join-Path $here $(if ($Tester) { 'signed-test' } else { 'signed' })
foreach ($d in @($toSign, $signed)) { if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null } }

# Windows PowerShell 5.1 (Desktop) has BigInteger in System.Numerics; PowerShell 7 (Core) elsewhere
$refs = if ($PSVersionTable.PSEdition -eq 'Core') { @('System.Runtime.Numerics', 'System.Security.Cryptography', 'System.Runtime', 'System.Collections') } else { @('System.Numerics') }
Add-Type -ReferencedAssemblies $refs -TypeDefinition @'
using System;
using System.Numerics;
using System.Security.Cryptography;

namespace WarpforgeRevival
{
    public static class Ed25519
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger Q = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D = Mod(-121665 * Inv(121666));
        private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
        private static readonly BigInteger[] G = MakeBase();

        private static BigInteger Mod(BigInteger x) { BigInteger r = x % P; return r.Sign < 0 ? r + P : r; }
        private static BigInteger Inv(BigInteger x) { return BigInteger.ModPow(Mod(x), P - 2, P); }

        private static BigInteger[] MakeBase()
        {
            BigInteger y = Mod(4 * Inv(5));
            BigInteger x = RecoverX(y, 0);
            return new BigInteger[] { x, y, BigInteger.One, Mod(x * y) };
        }

        // points in extended coordinates (X, Y, Z, T), x = X/Z, y = Y/Z, x*y = T/Z
        private static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
        {
            BigInteger a = Mod((p[1] - p[0]) * (q[1] - q[0]));
            BigInteger b = Mod((p[1] + p[0]) * (q[1] + q[0]));
            BigInteger c = Mod(2 * p[3] * q[3] * D);
            BigInteger d = Mod(2 * p[2] * q[2]);
            BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
            return new BigInteger[] { Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h) };
        }

        private static BigInteger[] Mul(BigInteger s, BigInteger[] p)
        {
            BigInteger[] q = new BigInteger[] { BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero };
            while (s.Sign > 0)
            {
                if (!s.IsEven) q = Add(q, p);
                p = Add(p, p);
                s >>= 1;
            }
            return q;
        }

        private static bool Equal(BigInteger[] p, BigInteger[] q)
        {
            return Mod(p[0] * q[2] - q[0] * p[2]).IsZero && Mod(p[1] * q[2] - q[1] * p[2]).IsZero;
        }

        private static BigInteger RecoverX(BigInteger y, int sign)
        {
            if (y >= P) return BigInteger.MinusOne;
            BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
            if (x2.IsZero) return sign == 0 ? BigInteger.Zero : BigInteger.MinusOne;
            BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
            if (!Mod(x * x - x2).IsZero) return BigInteger.MinusOne;
            if ((int)(x & 1) != sign) x = P - x;
            return x;
        }

        private static byte[] Compress(BigInteger[] p)
        {
            BigInteger zi = Inv(p[2]);
            BigInteger x = Mod(p[0] * zi), y = Mod(p[1] * zi);
            byte[] b = ToBytes(y);
            if (!x.IsEven) b[31] |= 0x80;
            return b;
        }

        private static BigInteger[] Decompress(byte[] s)
        {
            if (s == null || s.Length != 32) return null;
            byte[] c = (byte[])s.Clone();
            int sign = c[31] >> 7;
            c[31] &= 0x7F;
            BigInteger y = FromBytes(c);
            BigInteger x = RecoverX(y, sign);
            if (x.Sign < 0) return null;
            return new BigInteger[] { x, y, BigInteger.One, Mod(x * y) };
        }

        private static BigInteger FromBytes(byte[] b)
        {
            byte[] u = new byte[b.Length + 1];
            Array.Copy(b, u, b.Length);
            return new BigInteger(u);
        }

        private static byte[] ToBytes(BigInteger x)
        {
            byte[] raw = x.ToByteArray();
            byte[] o = new byte[32];
            Array.Copy(raw, o, Math.Min(32, raw.Length));
            return o;
        }

        private static byte[] Sha512(params byte[][] parts)
        {
            using (SHA512 h = SHA512.Create())
            {
                int n = 0;
                foreach (byte[] p in parts) n += p.Length;
                byte[] all = new byte[n];
                int at = 0;
                foreach (byte[] p in parts) { Array.Copy(p, 0, all, at, p.Length); at += p.Length; }
                return h.ComputeHash(all);
            }
        }

        private static BigInteger Scalar(byte[] h32)
        {
            byte[] a = new byte[32];
            Array.Copy(h32, a, 32);
            a[0] &= 248; a[31] &= 127; a[31] |= 64;
            return FromBytes(a);
        }

        /// <summary>The public key (32 bytes) for a secret key (32 random bytes).</summary>
        public static byte[] PublicKey(byte[] secret)
        {
            byte[] h = Sha512(secret);
            return Compress(Mul(Scalar(h), G));
        }

        /// <summary>The 64-byte signature of a message.</summary>
        public static byte[] Sign(byte[] secret, byte[] message)
        {
            byte[] h = Sha512(secret);
            BigInteger a = Scalar(h);
            byte[] prefix = new byte[32];
            Array.Copy(h, 32, prefix, 0, 32);
            byte[] A = Compress(Mul(a, G));
            BigInteger r = FromBytes(Sha512(prefix, message)) % Q;
            byte[] R = Compress(Mul(r, G));
            BigInteger k = FromBytes(Sha512(R, A, message)) % Q;
            BigInteger s = (r + k * a) % Q;
            byte[] sig = new byte[64];
            Array.Copy(R, sig, 32);
            Array.Copy(ToBytes(s), 0, sig, 32, 32);
            return sig;
        }

        /// <summary>True when the signature is the key's signature of the message.</summary>
        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != 32 || signature == null || signature.Length != 64) return false;
            BigInteger[] A = Decompress(publicKey);
            if (A == null) return false;
            byte[] Rs = new byte[32], Ss = new byte[32];
            Array.Copy(signature, Rs, 32);
            Array.Copy(signature, 32, Ss, 0, 32);
            BigInteger[] R = Decompress(Rs);
            if (R == null) return false;
            BigInteger s = FromBytes(Ss);
            if (s >= Q) return false;
            BigInteger k = FromBytes(Sha512(Rs, publicKey, message)) % Q;
            return Equal(Mul(s, G), Add(R, Mul(k, A)));
        }
    }
}

'@

$sha = [System.Security.Cryptography.SHA256]::Create()
$latin1 = [Text.Encoding]::GetEncoding(28591)
$magic = [Text.Encoding]::ASCII.GetBytes('WFRSIG01')

function Message([string]$name, [string]$version, [byte[]]$hash) {
    $head = [Text.Encoding]::UTF8.GetBytes("Warpforge Revival mod v1`n$name`n$version`n")
    $all = New-Object byte[] ($head.Length + $hash.Length)
    [Array]::Copy($head, 0, $all, 0, $head.Length)
    [Array]::Copy($hash, 0, $all, $head.Length, $hash.Length)
    return ,$all
}

function VersionOf([byte[]]$data, [int]$length) {
    $m = [regex]::Match($latin1.GetString($data, 0, $length), '(?:WF|Warpforge) Revival[\s\S](\d+(?:\.\d+){1,3})(-[a-z][a-z0-9]*)?')
    if ($m.Success) { return $m.Groups[1].Value + $m.Groups[2].Value }
    return $null
}

function KeyId([byte[]]$public) { $id = New-Object byte[] 8; [Array]::Copy($sha.ComputeHash($public), $id, 8); return ,$id }

# what a signed file says about itself: $null when it is not signed with one of the keys, else its version
function SignedVersion([string]$path, $publicKeys) {
    $data = [IO.File]::ReadAllBytes($path)
    if ($data.Length -le 80 -or $latin1.GetString($data, $data.Length - 8, 8) -ne 'WFRSIG01') { return $null }
    $body = $data.Length - 80
    $version = VersionOf $data $body
    if (-not $version) { return $null }
    $sig = New-Object byte[] 64; [Array]::Copy($data, $body, $sig, 0, 64)
    $message = Message ([IO.Path]::GetFileName($path)) $version ($sha.ComputeHash($data, 0, $body))
    foreach ($pk in $publicKeys) { if ([WarpforgeRevival.Ed25519]::Verify($pk, $message, $sig)) { return $version } }
    return $null
}

$publicFile = Join-Path $keys 'public-keys.txt'
if (-not (Test-Path $publicFile)) { Write-Host "  No keys\public-keys.txt. Run keys\make signing keys.bat first."; exit 1 }
$publicKeys = @()
foreach ($line in Get-Content $publicFile) {
    $parts = $line.Trim() -split '\s+'
    if ($parts.Count -eq 2) { $publicKeys += ,([Convert]::FromBase64String($parts[1])) }
}

# the builds' file names: WFRevival(.Android).dll from the rename on (mod 0.15.0), WarpforgeRevival(.Android).dll before
$names = @('WFRevival.dll', 'WFRevival.Android.dll', 'WarpforgeRevival.dll', 'WarpforgeRevival.Android.dll')
$failed = $false

if ($Check) {
    $found = 0
    foreach ($n in $names) {
        $p = Join-Path $signed $n
        if (-not (Test-Path $p)) { continue }
        $found++
        $v = SignedVersion $p $publicKeys
        if ($v) { Write-Host "  $n $v - signature good" } else { Write-Host "  $n - NOT signed with your key"; $failed = $true }
    }
    if ($found -eq 0) { Write-Host "  Nothing in signed\."; exit 1 }
    if ($failed) { exit 1 }
    exit 0
}

$keyFile = Join-Path $keys 'mod-signing-key.txt'
if (-not (Test-Path $keyFile)) { Write-Host "  No keys\mod-signing-key.txt. Run keys\make signing keys.bat first."; exit 1 }
$secret = [Convert]::FromBase64String((Get-Content -Raw $keyFile).Trim())
if ($secret.Length -ne 32) { Write-Host "  The signing key file is damaged."; exit 1 }
$public = [WarpforgeRevival.Ed25519]::PublicKey($secret)
$keyId = KeyId $public

$found = 0
foreach ($n in $names) {
    $path = Join-Path $toSign $n
    if (-not (Test-Path $path)) { continue }
    $found++
    $data = [IO.File]::ReadAllBytes($path)
    if ($data.Length -gt 80 -and $latin1.GetString($data, $data.Length - 8, 8) -eq 'WFRSIG01') {
        Write-Host "  $n in to-sign is already signed; copied to signed\ as it is."
        Copy-Item -Force $path (Join-Path $signed $n)
        continue
    }
    $version = VersionOf $data $data.Length
    if (-not $version) { Write-Host "  $n names no version; not signed."; $failed = $true; continue }
    $message = Message $n $version ($sha.ComputeHash($data))
    $sig = [WarpforgeRevival.Ed25519]::Sign($secret, $message)
    if (-not [WarpforgeRevival.Ed25519]::Verify($public, $message, $sig)) { Write-Host "  Signing $n went wrong; not signed."; $failed = $true; continue }
    $out = New-Object byte[] ($data.Length + 80)
    [Array]::Copy($data, 0, $out, 0, $data.Length)
    [Array]::Copy($sig, 0, $out, $data.Length, 64)
    [Array]::Copy($keyId, 0, $out, $data.Length + 64, 8)
    [Array]::Copy($magic, 0, $out, $data.Length + 72, 8)
    $dest = Join-Path $signed $n
    [IO.File]::WriteAllBytes("$dest.signing", $out)
    Move-Item -Force "$dest.signing" $dest
    Write-Host "  Signed $n $version -> signed\$n"
}
if ($found -eq 0) { Write-Host "  Nothing to sign in to-sign\."; exit 1 }
if ($failed) { exit 1 }
exit 0
