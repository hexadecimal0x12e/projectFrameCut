codename 'projectFrameCut'에 관하여

저작권 (c) hexadecimal0x12e 2025.

projectFrameCut는 **GNU GPL v2 (또는 그 이후 버전)** 하에 오픈소스로 배포됩니다. 소스 코드는 다음에서 확인할 수 있습니다: https://github.com/hexadecimal0x12e/projectFrameCut

**projectFrameCut는 아직 개발 중이며, 어떠한 생산 환경에서도 사용하지 마시기 바랍니다.** 또한 projectFrameCut의 오류로 인해 작업에 지장이 발생하더라도 저희는 어떠한 보증도 제공하지 않습니다.

소프트웨어 개발이 길고 어려운 작업이라는 것을 잘 알고 있습니다. 버그를 발견하거나 좋은 아이디어가 있다면, 저희에게 [issue](https://github.com/hexadecimal0x12e/projectFrameCut/issues/new)를 남겨 주세요.

projectFrameCut의 목표는 강력하고, 배우기 쉬우며 완전히 자유로운 비디오 편집 소프트웨어가 되는 것입니다. 우리는 이 목표를 달성하기 위해 노력하고 있습니다.

타사 라이브러리에 대한 감사

이 프로젝트는 기본적인 프레임 추출 및 처리를 위해 [FFmpeg](https://ffmpeg.org)와 [SixLabors.ImageSharp](https://sixlabors.com/products/imagesharp/) 및 관련 라이브러리를 사용하며, FFmpeg 호출에는 `FFmpeg_droidFix.AutoGen`을 사용합니다.

Windows 타겟에서는 하드웨어 가속을 위해 [ILGPU](https://github.com/m4rs-mt/ILGPU/)를 사용합니다.

Android 타겟에서는 크래시 로그 처리를 위해 [Fishnet](https://github.com/Kyant0/Fishnet)을 사용합니다.
