use serde;
fn model_files(lang: &str) -> Result<(&'static str, &'static str, &'static str), String> {
    const DET: &str = "pp-ocrv5_mobile_det.onnx";

    match lang {
        "en" => Ok((DET, "en_pp-ocrv5_mobile_rec.onnx", "ppocrv5_en_dict.txt")),
        "ru" => Ok((
            DET,
            "eslav_pp-ocrv5_mobile_rec.onnx",
            "ppocrv5_eslav_dict.txt",
        )),
        // Общая модель PP-OCRv5: китайский + японский + английский
        "ch" => Ok((DET, "pp-ocrv5_mobile_rec.onnx", "ppocrv5_dict.txt")),
        "ko" => Ok((
            DET,
            "korean_pp-ocrv5_mobile_rec.onnx",
            "ppocrv5_korean_dict.txt",
        )),
        other => Err(format!("Неизвестный язык OCR: {other}")),
    }
}

/// Результат OCR: текст и рамка, где он был найден (доли зоны, 0..1).
#[derive(serde::Serialize)]
struct OcrOutput {
    text: String,
    bbox: Option<OcrBox>,
    regions: Vec<OcrRegion>,
}

#[derive(serde::Serialize)]
struct OcrRegion {
    text: String,
    bbox: OcrBox,
    confidence: f32,
}

#[derive(Clone, serde::Serialize)]
struct OcrBox {
    x: f64,
    y: f64,
    w: f64,
    h: f64,
}

fn normalized_ocr_box(points: &[(f64, f64)], width: f64, height: f64) -> Option<OcrBox> {
    if points.is_empty() || width <= 0.0 || height <= 0.0 {
        return None;
    }
    let x0 = (points.iter().map(|p| p.0).fold(f64::INFINITY, f64::min) / width).clamp(0.0, 1.0);
    let x1 = (points.iter().map(|p| p.0).fold(f64::NEG_INFINITY, f64::max) / width).clamp(0.0, 1.0);
    let y0 = (points.iter().map(|p| p.1).fold(f64::INFINITY, f64::min) / height).clamp(0.0, 1.0);
    let y1 =
        (points.iter().map(|p| p.1).fold(f64::NEG_INFINITY, f64::max) / height).clamp(0.0, 1.0);
    (x1 > x0 && y1 > y0).then_some(OcrBox {
        x: x0,
        y: y0,
        w: x1 - x0,
        h: y1 - y0,
    })
}

/// Собирает все точки (x, y) из JSON-представления рамки. Понимает и
/// `{"x":..,"y":..}`, и `[x, y]` — какой бы вид ни имел BoundingBox.
fn collect_points(v: &serde_json::Value, out: &mut Vec<(f64, f64)>) {
    match v {
        serde_json::Value::Object(map) => {
            if let (Some(x), Some(y)) = (
                map.get("x").and_then(|n| n.as_f64()),
                map.get("y").and_then(|n| n.as_f64()),
            ) {
                out.push((x, y));
                return;
            }
            for child in map.values() {
                collect_points(child, out);
            }
        }
        serde_json::Value::Array(arr) => {
            if arr.len() == 2 && arr.iter().all(|n| n.is_number()) {
                if let (Some(x), Some(y)) = (arr[0].as_f64(), arr[1].as_f64()) {
                    out.push((x, y));
                    return;
                }
            }
            for child in arr {
                collect_points(child, out);
            }
        }
        _ => {}
    }
}


use oar_ocr::prelude::*;
use std::{collections::HashMap, io::{self, BufRead, Write}};
#[derive(serde::Deserialize)]
struct Input { path: String, lang: String }
fn main() {
    let mut engines: HashMap<String, OAROCR> = HashMap::new();
    for line in io::stdin().lock().lines() {
        let Ok(line) = line else { break };
        let response = (|| -> Result<OcrOutput, String> {
            let input: Input = serde_json::from_str(&line).map_err(|e| e.to_string())?;
            let img = image::open(&input.path).map_err(|e| e.to_string())?.to_rgb8();
            let (width, height) = (img.width() as f64, img.height() as f64);
            if !engines.contains_key(&input.lang) {
                let (det, rec, dict) = model_files(&input.lang)?;
                engines.insert(input.lang.clone(), OAROCRBuilder::new(det, rec, dict).build().map_err(|e| e.to_string())?);
            }
            let results = engines[&input.lang].predict(vec![img]).map_err(|e| e.to_string())?;
            let mut regions = Vec::new();
            if let Some(res) = results.first() {
                for region in &res.text_regions {
                    if let Some((text, confidence)) = region.text_with_confidence() {
                        let value = serde_json::to_value(&region.bounding_box).map_err(|e| e.to_string())?;
                        let mut points = Vec::new();
                        collect_points(&value, &mut points);
                        if let Some(bbox) = normalized_ocr_box(&points, width, height) {
                            regions.push(OcrRegion { text: text.to_owned(), bbox, confidence });
                        }
                    }
                }
            }
            let text = regions.iter().map(|r| r.text.as_str()).collect::<Vec<_>>().join("\n");
            Ok(OcrOutput { text, bbox: None, regions })
        })();
        let value = match response { Ok(value) => serde_json::json!({"ok":true,"result":value}), Err(error) => serde_json::json!({"ok":false,"error":error}) };
        println!("OCR_RESULT:{}", value);
        let _ = io::stdout().flush();
    }
}
