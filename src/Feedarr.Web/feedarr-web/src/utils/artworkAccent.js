const SAMPLE_SIZE = 24;
const HUE_BUCKET_COUNT = 24;
const MAX_CACHE_ENTRIES = 128;

const artworkAccentCache = new Map();

function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}

function rgbToHsl(red, green, blue) {
  const r = red / 255;
  const g = green / 255;
  const b = blue / 255;
  const maximum = Math.max(r, g, b);
  const minimum = Math.min(r, g, b);
  const delta = maximum - minimum;
  const lightness = (maximum + minimum) / 2;

  if (delta === 0) return { hue: 0, saturation: 0, lightness };

  const saturation = delta / (1 - Math.abs(2 * lightness - 1));
  let hue;
  if (maximum === r) hue = 60 * (((g - b) / delta) % 6);
  else if (maximum === g) hue = 60 * ((b - r) / delta + 2);
  else hue = 60 * ((r - g) / delta + 4);

  return {
    hue: hue < 0 ? hue + 360 : hue,
    saturation,
    lightness,
  };
}

function rememberAccent(cacheKey, accent) {
  if (artworkAccentCache.size >= MAX_CACHE_ENTRIES) {
    const oldestKey = artworkAccentCache.keys().next().value;
    artworkAccentCache.delete(oldestKey);
  }
  artworkAccentCache.set(cacheKey, accent);
  return accent;
}

export function selectArtworkAccent(pixels) {
  const buckets = Array.from({ length: HUE_BUCKET_COUNT }, () => ({
    score: 0,
    hueX: 0,
    hueY: 0,
    saturation: 0,
    lightness: 0,
  }));

  for (let index = 0; index < pixels.length; index += 4) {
    const alpha = pixels[index + 3] / 255;
    if (alpha < 0.75) continue;

    const hsl = rgbToHsl(pixels[index], pixels[index + 1], pixels[index + 2]);
    if (hsl.lightness < 0.07 || hsl.lightness > 0.94 || hsl.saturation < 0.12) continue;

    const usefulLightness = 1 - Math.min(1, Math.abs(hsl.lightness - 0.55) / 0.55);
    const weight = alpha * (0.18 + hsl.saturation ** 1.35) * (0.35 + usefulLightness);
    const bucketIndex = Math.min(
      HUE_BUCKET_COUNT - 1,
      Math.floor((hsl.hue / 360) * HUE_BUCKET_COUNT)
    );
    const bucket = buckets[bucketIndex];
    const radians = (hsl.hue * Math.PI) / 180;
    bucket.score += weight;
    bucket.hueX += Math.cos(radians) * weight;
    bucket.hueY += Math.sin(radians) * weight;
    bucket.saturation += hsl.saturation * weight;
    bucket.lightness += hsl.lightness * weight;
  }

  const winner = buckets.reduce(
    (best, bucket) => (bucket.score > best.score ? bucket : best),
    buckets[0]
  );
  if (!winner || winner.score <= 0) return null;

  const rawHue = (Math.atan2(winner.hueY, winner.hueX) * 180) / Math.PI;
  const hue = Math.round(rawHue < 0 ? rawHue + 360 : rawHue);
  const saturation = Math.round(clamp(winner.saturation / winner.score, 0.46, 0.68) * 100);
  const lightness = Math.round(clamp(winner.lightness / winner.score, 0.46, 0.58) * 100);
  return `hsl(${hue} ${saturation}% ${lightness}%)`;
}

export function extractArtworkAccent(image, cacheKey) {
  const key = String(cacheKey || image?.currentSrc || image?.src || "");
  if (!key || !image) return null;
  if (artworkAccentCache.has(key)) return artworkAccentCache.get(key);

  try {
    const canvas = document.createElement("canvas");
    canvas.width = SAMPLE_SIZE;
    canvas.height = SAMPLE_SIZE;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) return rememberAccent(key, null);

    context.drawImage(image, 0, 0, SAMPLE_SIZE, SAMPLE_SIZE);
    const pixels = context.getImageData(0, 0, SAMPLE_SIZE, SAMPLE_SIZE).data;
    return rememberAccent(key, selectArtworkAccent(pixels));
  } catch {
    // Cross-origin images without suitable CORS headers taint the canvas.
    return rememberAccent(key, null);
  }
}
