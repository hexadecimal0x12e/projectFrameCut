import os
import subprocess

input_dir = "."       
dpi = 600   # 指定分辨率


for file in os.listdir(input_dir):
    if file.lower().endswith(".svg"):
        svg_path = os.path.join(input_dir, file)
        png_path = os.path.join(input_dir, file.replace(".svg", "_png.png"))

        print(f"Converting {svg_path} -> {png_path}")

        subprocess.run([
            "inkscape",
            svg_path,
            "--export-type=png",
            f"--export-dpi={dpi}",
            f"--export-filename={png_path}"
        ])

print("转换完成！")
