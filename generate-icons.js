const sharp = require('sharp');
const fs = require('fs');
const path = require('path');

const svgPath = path.join(__dirname, 'src', 'SqlAgMonitor', 'Assets', 'app-icon.svg');
const assetsDir = path.join(__dirname, 'src', 'SqlAgMonitor', 'Assets');
const svgBuffer = fs.readFileSync(svgPath);

const sizes = [16, 32, 48, 64, 128, 256];

async function main() {
    // Generate PNGs at each size
    const pngBuffers = [];
    for (const size of sizes) {
        const buf = await sharp(svgBuffer, { density: 300 })
            .resize(size, size)
            .png()
            .toBuffer();
        pngBuffers.push(buf);
        console.log(`Generated ${size}x${size} PNG`);
    }

    // Create the main icon PNG (256x256 for Avalonia TrayIcon / window icon)
    const mainPng = path.join(assetsDir, 'app-icon.png');
    await sharp(svgBuffer, { density: 300 })
        .resize(256, 256)
        .png()
        .toFile(mainPng);
    console.log('Created app-icon.png (256x256)');

    // Build ICO manually (ICO format: header + directory entries + PNG data)
    const count = pngBuffers.length;
    const headerSize = 6;
    const dirEntrySize = 16;
    const dirSize = dirEntrySize * count;
    let dataOffset = headerSize + dirSize;

    // ICO header: reserved(2) + type(2) + count(2)
    const header = Buffer.alloc(headerSize);
    header.writeUInt16LE(0, 0);      // reserved
    header.writeUInt16LE(1, 2);      // type: 1 = ICO
    header.writeUInt16LE(count, 4);  // image count

    const dirEntries = [];
    const imageDataParts = [];

    for (let i = 0; i < count; i++) {
        const size = sizes[i];
        const pngBuf = pngBuffers[i];

        const entry = Buffer.alloc(dirEntrySize);
        entry.writeUInt8(size >= 256 ? 0 : size, 0);   // width (0 = 256)
        entry.writeUInt8(size >= 256 ? 0 : size, 1);   // height (0 = 256)
        entry.writeUInt8(0, 2);                          // color palette
        entry.writeUInt8(0, 3);                          // reserved
        entry.writeUInt16LE(1, 4);                       // color planes
        entry.writeUInt16LE(32, 6);                      // bits per pixel
        entry.writeUInt32LE(pngBuf.length, 8);           // image data size
        entry.writeUInt32LE(dataOffset, 12);             // offset to image data

        dirEntries.push(entry);
        imageDataParts.push(pngBuf);
        dataOffset += pngBuf.length;
    }

    const icoBuffer = Buffer.concat([header, ...dirEntries, ...imageDataParts]);
    const icoPath = path.join(assetsDir, 'app-icon.ico');
    fs.writeFileSync(icoPath, icoBuffer);
    console.log('Created app-icon.ico');
    console.log('Done!');
}

main().catch(err => { console.error(err); process.exit(1); });
